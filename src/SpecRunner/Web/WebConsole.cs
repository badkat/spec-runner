using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpecRunner.Config;
using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Engine;
using SpecRunner.Graph;
using SpecRunner.Reconcile;
using SpecRunner.Records;

namespace SpecRunner.Web;

/// <summary>
/// The operator's console, server side.
///
/// The browser is a view onto the run and a set of requests to it - never a second executor. Every
/// endpoint that changes anything either writes a record the same way the operator's editor would
/// (feature 6.1) or sets a flag the runner reads at a step boundary (features 9.3, 8.8, 1.13).
/// Nothing here runs a step.
///
/// Loopback only, no authentication, single project: feature set aside by project_info.md's
/// non-goals, and binding to 127.0.0.1 is what keeps that honest.
/// </summary>
public sealed class WebConsole(
    AppConfig config,
    ConsoleBroker broker,
    RunControl control,
    RecordStore records,
    ArtifactStore artifacts,
    ProjectPaths paths,
    DependencyGraph graph,
    Func<ReconcileResult?> currentReconciliation)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WebApplication Build()
    {
        var builder = WebApplication.CreateBuilder();

        // Pillar 2 - the host's own console logging would be a third diagnostic channel. Clearing
        // the providers and installing a surface-routing one is what keeps the count at two.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SurfaceLoggerProvider());

        builder.WebHost.UseUrls(config.ListenUrl);
        builder.Services.AddSingleton(broker);

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(ConsolePage.Html, "text/html; charset=utf-8"));

        // Feature 8.3 - on connect the browser receives the complete history of the run so far,
        // then live events. A refresh mid-run must not produce a console showing half the story.
        app.MapGet("/api/events", StreamEvents);

        app.MapGet("/api/status", () => Results.Json(new
        {
            phase = control.Phase.ToString(),
            runId = records.Run.Id,
            sequence = records.Run.CurrentSequence,
            projectRoot = paths.Root,
            endpoint = config.BaseUrl,
            current = control.Current,
            block = control.Block,
            startRequested = control.StartRequested,
            stopRequested = control.StopRequested
        }, Json));

        app.MapGet("/api/plan", () =>
        {
            var reconciled = currentReconciliation();
            if (reconciled is null)
            {
                return Results.Json(new { ready = false }, Json);
            }

            return Results.Json(new
            {
                ready = true,
                definiteModelCalls = reconciled.DefiniteModelCalls,
                modelCallCountIsLowerBound = reconciled.ModelCallCountIsLowerBound,
                steps = reconciled.Plan.Select(p => new
                {
                    stepId = p.StepId,
                    target = p.IterationTarget,
                    description = p.Description,
                    action = p.Action.ToString().ToLowerInvariant(),
                    reason = p.Reason,
                    callsModel = p.CallsModel,
                    recordPath = p.RecordPath,
                    itemsPending = p.ItemsPending,
                    inputs = p.Inputs.Select(i => new { i.Kind, i.Name, i.Hash, i.Producer })
                }),
                orphans = reconciled.Orphans,
                handEdited = reconciled.HandEditedArtifacts,
                incompleteModelCalls = reconciled.IncompleteModelCalls,
                stateDivergences = reconciled.StateDivergences,
                invalidations = reconciled.Invalidations.Select(i => new
                {
                    stepId = i.StepId,
                    target = i.IterationTarget,
                    cause = i.Cause,
                    differingInput = i.DifferingInput,
                    expected = i.ExpectedHash,
                    actual = i.ActualHash,
                    path = i.Path
                })
            }, Json);
        });

        // Feature 1.10 - execution begins on an explicit operator start. The plan is rendered
        // first, and nothing runs until this is called.
        app.MapPost("/api/start", () =>
        {
            if (control.Phase != RunPhase.AwaitingStart)
            {
                return Results.BadRequest(new { error = $"The run is {control.Phase}, not awaiting start." });
            }

            control.RequestStart();
            return Results.Ok(new { started = true });
        });

        // Feature 9.3 - stop is a flag checked at step boundaries.
        app.MapPost("/api/stop", () =>
        {
            control.RequestStop();
            Emit.To(
                Surface.Console,
                EventKinds.PlanGate,
                "Stop requested. The in-flight step will run to its commit, then the process exits.",
                Emit.Fields("requested_utc", RunIdentity.TimestampUtc()));
            return Results.Ok(new { stopping = true });
        });

        // Feature 1.13 - the same code path as the startup projection rebuild, exposed for use
        // after mid-session edits.
        app.MapPost("/api/rebuild-state", () =>
        {
            control.RequestStateRebuild();
            return Results.Ok(new { queued = true });
        });

        // Feature 8.8 - "invalidate this step and everything downstream".
        app.MapPost("/api/invalidate", async (HttpRequest request) =>
        {
            var body = await ReadJson(request);
            var stepId = body.GetPropertyOrNull("stepId")?.GetString();
            if (stepId is null || !graph.Contains(stepId))
            {
                return Results.BadRequest(new { error = $"Unknown step id '{stepId}'." });
            }

            control.RequestInvalidation(new ForcedInvalidation(stepId, body.GetPropertyOrNull("target")?.GetString()));
            return Results.Ok(new
            {
                queued = true,
                note = "Applied at the next step boundary, then the run halts so replay can re-establish from there."
            });
        });

        // Feature 6.1 - a browser click is the server performing the same write the operator's
        // editor would have performed, through the same write-temp-then-rename mechanism.
        app.MapPost("/api/answer", async (HttpRequest request) => await SubmitAnswer(request));

        // Feature 8.7 - the provenance walker. The same information is hand-traversable on disk;
        // this is the convenience view, not the only way in.
        app.MapGet("/api/explain", (string path) =>
        {
            try
            {
                return Results.Json(ProvenanceWalker.Explain(artifacts, paths, path), Json);
            }
            catch (HaltException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Feature 8.6 - the per-step detail view, including the record that justified a skip.
        app.MapGet("/api/step", (string stepId, string? target) =>
        {
            if (!graph.Contains(stepId))
            {
                return Results.BadRequest(new { error = $"Unknown step id '{stepId}'." });
            }

            var step = graph.ById(stepId);
            var scopedTarget = string.IsNullOrEmpty(target) ? null : target;
            var completion = records.InForceCompletion(stepId, scopedTarget);

            return Results.Json(new
            {
                stepId,
                target = scopedTarget,
                step.Description,
                template = step.PromptTemplate is null ? null : $"prompts/{step.PromptTemplate}",
                iteratesOver = step.IteratesOver,
                guard = step.Guard?.Description,
                readsVariables = step.ReadsVariables,
                writesVariables = step.WritesVariables,
                readsArtifacts = step.ReadsArtifacts,
                writesArtifacts = step.WritesArtifacts,
                downstream = graph.DownstreamClosure(stepId),
                inForceRecord = completion is null ? null : new
                {
                    completion.Path,
                    completion.RunId,
                    completion.Sequence,
                    completion.TimestampUtc,
                    inputs = completion.Inputs.Select(i => new { i.Kind, i.Name, i.Hash, i.Producer }),
                    outputs = completion.OutputVariables.Select(v => new { v.Name, v.Hash }),
                    artifacts = completion.OutputArtifacts.Select(a => new { a.ArtifactId, a.Version, a.Path, a.Hash })
                },
                invalidations = records.ReadInvalidations(stepId, scopedTarget).Select(i => new
                {
                    i.Cause,
                    i.DifferingInput,
                    i.ExpectedHash,
                    i.ActualHash,
                    i.RaisedByStep,
                    i.TimestampUtc,
                    i.Path
                })
            }, Json);
        });

        // Reading a record or artifact in the browser is a convenience over opening it in an
        // editor; the file is the record either way (Pillar 7).
        app.MapGet("/api/file", (string path) =>
        {
            var absolute = paths.Absolute(path);
            if (!absolute.StartsWith(paths.Root, StringComparison.OrdinalIgnoreCase) || !File.Exists(absolute))
            {
                return Results.BadRequest(new { error = $"No such file inside the project: {path}" });
            }

            return Results.Text(File.ReadAllText(absolute), "text/plain; charset=utf-8");
        });

        return app;
    }

    private async Task StreamEvents(HttpContext http)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // The stream is open for as long as the browser is watching, which is longer than the run
        // itself. Linking to ApplicationStopping is what lets the process actually end when the
        // run does: without it, a shutdown waits forever on a request that never finishes, and
        // "stop" would silently mean "stop, unless a tab is open" - exactly the kind of quiet
        // partial behaviour Pillar 3 rejects.
        var lifetime = http.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            http.RequestAborted, lifetime.ApplicationStopping);
        var token = cancellation.Token;

        var (history, live) = broker.Subscribe();

        try
        {
            foreach (var e in history)
            {
                await WriteEvent(http, e, token);
            }

            await http.Response.WriteAsync(": end-of-history\n\n", token);
            await http.Response.Body.FlushAsync(token);

            await foreach (var e in live.ReadAllAsync(token))
            {
                await WriteEvent(http, e, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Either the operator closed the tab, or the process is ending. The application makes
            // no attempt to track whether a browser window remains open (feature 8.9).
        }
        finally
        {
            broker.Unsubscribe(live);
        }
    }

    private static async Task WriteEvent(HttpContext http, EmittedEvent e, CancellationToken cancellation)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sequence = e.Sequence,
            timestampUtc = e.TimestampUtc,
            surface = e.Surface.ToString().ToLowerInvariant(),
            kind = e.Kind,
            message = e.Message,
            fields = e.Fields.Select(f => new { key = f.Key, value = f.Value }),
            data = e.Data
        }, Json);

        await http.Response.WriteAsync($"data: {payload}\n\n", cancellation);
        await http.Response.Body.FlushAsync(cancellation);
    }

    private async Task<IResult> SubmitAnswer(HttpRequest request)
    {
        var body = await ReadJson(request);
        var stepId = body.GetPropertyOrNull("stepId")?.GetString();
        var target = body.GetPropertyOrNull("target")?.GetString();
        var answer = body.GetPropertyOrNull("answer")?.GetString();
        var versionElement = body.GetPropertyOrNull("version");

        if (stepId is null || answer is null || versionElement is null)
        {
            return Results.BadRequest(new { error = "stepId, version and answer are all required." });
        }

        var version = versionElement.Value.GetInt32();
        var scopedTarget = string.IsNullOrEmpty(target) ? null : target;

        var question = records.ReadQuestion(stepId, scopedTarget, version);
        if (question is null)
        {
            return Results.BadRequest(new { error = $"No question v{version} exists for '{stepId}'." });
        }

        if (!question.PermittedAnswers.Contains(answer))
        {
            return Results.BadRequest(new
            {
                error = $"'{answer}' is outside the closed set of permitted answers.",
                permitted = question.PermittedAnswers
            });
        }

        var existing = records.ReadAnswer(stepId, scopedTarget, version);
        if (existing is not null)
        {
            // Feature 6.1 - a matching re-write is a no-op, not an error. Harmless duplication is
            // not an event. A *differing* one is a conflict with an immutable decision, and this
            // application does not silently resolve that by picking one.
            if (existing.Answer == answer)
            {
                return Results.Ok(new { recorded = false, note = "An identical answer already exists; nothing to do." });
            }

            return Results.Conflict(new
            {
                error =
                    $"An answer already exists for this question and says '{existing.Answer}'. A decision, once " +
                    "answered, is in force and cannot be changed by writing a different answer (feature 6.3). " +
                    "Invalidate this step first - from the console or by deleting its completion record - and the " +
                    "question opens again as a new round.",
                existingAnswer = existing.Answer,
                existingRecord = existing.Path
            });
        }

        var record = records.WriteAnswer(stepId, scopedTarget, version, answer, AnswerRecord.SurfaceBrowser);
        return Results.Ok(new { recorded = true, path = record.Path });
    }

    private static async Task<JsonElement> ReadJson(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
    }
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertyOrNull(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.ValueKind != JsonValueKind.Null
            ? value
            : null;
}
