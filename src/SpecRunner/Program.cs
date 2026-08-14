using System.Diagnostics;
using SpecRunner.Config;
using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Engine;
using SpecRunner.Graph;
using SpecRunner.Llm;
using SpecRunner.Reconcile;
using SpecRunner.Records;
using SpecRunner.Web;
using SpecRunner.Workflow;

namespace SpecRunner;

/// <summary>
/// Startup, in the order the pillars require it.
///
/// Everything that happens before the browser console can exist belongs to the terminal
/// (feature 8.9), and that is most of this file: config resolution, the instance lock, graph
/// validation, the project scan, port binding. From the moment the server is listening, workflow
/// state belongs to the browser and the terminal falls silent about it.
///
/// Feature 9.5 - an unhandled exception terminates the process after flushing both surfaces. There
/// is no top-level catch that continues.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var run = RunIdentity.New();
        var broker = new ConsoleBroker();
        Emit.Initialize(run, broker);

        InstanceLock? instanceLock = null;

        try
        {
            return Start(args, run, broker, ref instanceLock);
        }
        catch (HaltException halt)
        {
            // A halt reached the top without a run log to write it to, or after the run log was
            // already flushed. Either way the terminal owns process-ending events.
            Emit.To(
                Surface.Terminal,
                EventKinds.Fatal,
                halt.Message,
                Emit.Fields(
                    "run_id", run.Id,
                    "step_id", halt.StepId ?? "-",
                    "iteration_target", halt.IterationTarget ?? "-"));

            Emit.To(Surface.Terminal, EventKinds.Shutdown, "Halted. Nothing further will run.");
            return 1;
        }
        catch (Exception ex)
        {
            Emit.To(
                Surface.Terminal,
                EventKinds.Fatal,
                $"Unhandled exception. This is a defect, not a condition.\n{ex}",
                Emit.Fields("run_id", run.Id, "exception", ex.GetType().FullName ?? "(unknown)"));

            Emit.To(Surface.Terminal, EventKinds.Shutdown, "Terminated by an unhandled exception.");
            return 2;
        }
        finally
        {
            Emit.Flush();
            instanceLock?.Dispose();
        }
    }

    private static int Start(string[] args, RunIdentity run, ConsoleBroker broker, ref InstanceLock? instanceLock)
    {
        var options = CommandLine.Parse(args);
        if (options.ShowHelp)
        {
            Emit.To(Surface.Terminal, EventKinds.Startup, CommandLine.HelpText);
            return 0;
        }

        Emit.To(
            Surface.Terminal,
            EventKinds.Startup,
            "Spec Runner starting.",
            Emit.Fields("run_id", run.Id, "pid", Environment.ProcessId.ToString(), "utc", RunIdentity.TimestampUtc()));

        // ---- configuration, echoed with secrets elided (feature 8.10) ----
        var config = AppConfig.Resolve(options.ConfigPath);
        foreach (var value in config.Resolved)
        {
            Emit.To(
                Surface.Terminal,
                EventKinds.Config,
                $"{value.Key} = {(value.Secret ? Elide(value.Value) : value.Value)}",
                Emit.Fields("source", value.Source));
        }

        // Configuration is checked before anything is created. A run that cannot start should
        // leave no trace of having tried: creating the project directory first would scatter
        // empty trees and orphan run logs wherever the operator happened to be standing.
        RequirePromptsDirectory(config);

        var paths = new ProjectPaths(config.ProjectDirectory);
        Directory.CreateDirectory(paths.Root);

        var runLog = new RunLog(paths, run, paths.Root, config.BaseUrl);
        Emit.AttachRunLog(runLog);
        Emit.To(
            Surface.Terminal,
            EventKinds.SelfCheck,
            $"Run log: {runLog.Path_}",
            Emit.Fields("check", "run-log"));

        // ---- single instance (feature 9.1) ----
        instanceLock = InstanceLock.Acquire(paths.Root, run.Id, options.ClearStaleLock);
        Emit.To(
            Surface.Terminal,
            EventKinds.SelfCheck,
            $"Instance lock held: {InstanceLock.PathFor(paths.Root)}",
            Emit.Fields("check", "instance-lock"));

        // ---- the graph, validated before the web server binds (feature 1.2) ----
        var steps = Methodology.Steps();
        var graph = DependencyGraph.Build(steps);
        Emit.To(
            Surface.Terminal,
            EventKinds.GraphValidation,
            $"Dependency graph valid: {steps.Count} steps, {graph.VariableProducers.Count} variables, "
            + $"{graph.ArtifactProducers.Count} artifacts.",
            Emit.Fields("check", "dependency-graph"));

        var records = new RecordStore(paths, run);
        var artifacts = new ArtifactStore(paths);
        var control = new RunControl();

        // ---- reconciliation, in full, before anything executes ----
        var reconciler = new Reconciler(graph, records, artifacts, paths, config.PromptsDirectory);
        Emit.To(Surface.Console, EventKinds.ReconcileStarted, "Reconciling records against the dependency graph.");

        var reconciled = reconciler.Run();
        ReportReconciliation(reconciled, paths, run);

        // Feature 6.5 - a block already pending when the run begins is reported by the terminal,
        // in the same bucket as other startup self-checks. Mid-run blocks are the browser's alone.
        foreach (var block in reconciled.StartupBlocks)
        {
            Emit.To(
                Surface.Terminal,
                EventKinds.StartupBlock,
                $"A question is already pending and unanswered: {block.Question}",
                Emit.Fields(
                    "step_id", block.StepId,
                    "iteration_target", block.IterationTarget ?? "-",
                    "question_record", block.QuestionPath));
        }

        // ---- bind the fixed port (feature 9.2) ----
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var llm = new LlmClient(http, config.BaseUrl, config.ApiKey, records);

        var console = new WebConsole(config, broker, control, records, artifacts, paths, graph, () => reconciled);
        var app = console.Build();

        try
        {
            app.Start();
        }
        catch (Exception ex)
        {
            throw new HaltException(
                $"Could not bind {config.ListenUrl}: {ex.Message}\n" +
                "The port is fixed and there is no fallback. Falling forward to another port would be silent " +
                "recovery, and it would leave a bookmarked console pointed at nothing (feature 9.2).", ex);
        }

        Emit.To(
            Surface.Terminal,
            EventKinds.PortBinding,
            $"Console listening on {config.ListenUrl}",
            Emit.Fields("check", "port-binding", "port", config.Port.ToString()));

        LaunchBrowser(config.ListenUrl);

        // ---- the run itself ----
        var runner = new Runner(graph, reconciler, records, artifacts, llm, control, paths, config.PromptsDirectory);
        var exitCode = AwaitStartThenExecute(reconciled, runner, control, records, run);

        Emit.To(
            Surface.Terminal,
            EventKinds.Shutdown,
            exitCode == 0
                ? $"Run {run.Id} ended: {control.Phase}. Console log: {runLog.Path_}"
                : $"Run {run.Id} halted. Console log: {runLog.Path_}",
            Emit.Fields("phase", control.Phase.ToString(), "exit_code", exitCode.ToString()));

        app.StopAsync().GetAwaiter().GetResult();
        return exitCode;
    }

    /// <summary>
    /// Feature 1.10 - execution begins on an explicit operator start, after the plan has been
    /// rendered. Feature 9.5 - a halt inside the run is written to the run log, with the step and
    /// the inputs in flight, before the process ends.
    /// </summary>
    private static int AwaitStartThenExecute(
        ReconcileResult reconciled,
        Runner runner,
        RunControl control,
        RecordStore records,
        RunIdentity run)
    {
        control.Phase = RunPhase.AwaitingStart;
        Emit.To(
            Surface.Console,
            EventKinds.PlanGate,
            "The plan above is complete. Nothing runs until you start it.",
            Emit.Fields(
                "rows", reconciled.Plan.Count.ToString(),
                "to_execute", reconciled.Plan.Count(p => p.Action == StepAction.Execute).ToString(),
                "model_calls", reconciled.DefiniteModelCalls.ToString()));

        while (!control.StartRequested)
        {
            if (control.StopRequested)
            {
                Emit.To(Surface.Console, EventKinds.RunStopped, "Stopped before the run began.");
                control.Phase = RunPhase.Stopped;
                return 0;
            }

            // Feature 8.8 / 1.13 - the operator's controls work at the gate too, and applying one
            // here re-renders rather than halts, because nothing has run yet to be invalidated by it.
            if (control.HasPendingInvalidations)
            {
                while (control.TryTakeInvalidation(out var forced))
                {
                    runner.ApplyForcedInvalidation(forced);
                }

                Emit.To(
                    Surface.Console,
                    EventKinds.PlanGate,
                    "Invalidation applied before the run began. Restart the process to reconcile against it: " +
                    "workflow position is derived from the filesystem, so the new plan comes from a new startup.");
            }

            if (control.TakeStateRebuildRequest())
            {
                StateProjection.Write(records.Paths, reconciled.State, reconciled.Artifacts, run);
            }

            Thread.Sleep(100);
        }

        try
        {
            runner.Execute(reconciled);
            return 0;
        }
        catch (HaltException halt)
        {
            control.Phase = RunPhase.Halted;
            var current = control.Current;

            var detail =
                $"{halt.Message}\n\n" +
                $"step: {halt.StepId ?? current?.StepId ?? "(between steps)"}\n" +
                $"iteration target: {halt.IterationTarget ?? current?.IterationTarget ?? "-"}\n" +
                $"run: {run.Id}\n" +
                "inputs in flight:\n" +
                string.Join("\n", (current?.InputHashes ?? []).Select(h => $"  {h.Key} = {h.Value}"));

            var outcome = records.WriteRunOutcome("halted", detail, halt.StepId ?? current?.StepId, halt.IterationTarget);

            Emit.To(
                Surface.Console,
                EventKinds.RunHalted,
                detail,
                Emit.Fields("outcome_record", outcome, "run_id", run.Id));

            return 1;
        }
    }

    private static void ReportReconciliation(ReconcileResult reconciled, ProjectPaths paths, RunIdentity run)
    {
        foreach (var invalidation in reconciled.Invalidations)
        {
            Emit.To(
                Surface.Console,
                EventKinds.RecordInvalidated,
                $"{invalidation.StepId}{(invalidation.IterationTarget is null ? "" : $" [{invalidation.IterationTarget}]")}"
                + $" — {invalidation.Cause}",
                Emit.Fields(
                    "cause", invalidation.Cause,
                    "differing_input", invalidation.DifferingInput,
                    "expected", invalidation.ExpectedHash,
                    "actual", invalidation.ActualHash,
                    "superseded_record", invalidation.TargetRecordPath,
                    "invalidation_record", invalidation.Path));
        }

        foreach (var artifact in reconciled.HandEditedArtifacts)
        {
            Emit.To(
                Surface.Console,
                EventKinds.RecordInvalidated,
                $"{artifact} was edited by a person. Its content is taken as truth; everything derived from the "
                + "earlier content is invalidated.",
                Emit.Fields("artifact", artifact, "cause", InvalidationCause.ArtifactHandEdited));
        }

        foreach (var orphan in reconciled.Orphans)
        {
            Emit.To(
                Surface.Console,
                EventKinds.OrphanArtifact,
                $"{orphan.RelativePath} is named by no honored record. It will never be loaded as input.",
                Emit.Fields("likely_run", orphan.LikelyRunId, "producing_step", orphan.ProducingStepId));
        }

        foreach (var incomplete in reconciled.IncompleteModelCalls)
        {
            Emit.To(
                Surface.Console,
                EventKinds.LlmCondition,
                $"{incomplete} records a model call that was initiated and never completed. "
                + "The exact payload that was in flight is in that file.",
                Emit.Fields("request_record", incomplete));
        }

        if (reconciled.StateDivergences.Count > 0)
        {
            Emit.To(
                Surface.Console,
                EventKinds.StateDiff,
                "The state file on disk diverges from the reconstruction:\n  "
                + string.Join("\n  ", reconciled.StateDivergences),
                Emit.Fields("state_file", paths.Relative(paths.StateFile)));
        }

        StateProjection.Write(paths, reconciled.State, reconciled.Artifacts, run);

        Emit.To(
            Surface.Console,
            EventKinds.Plan,
            Reconciler.RenderPlan(reconciled),
            Emit.Fields(
                "rows", reconciled.Plan.Count.ToString(),
                "model_calls", reconciled.DefiniteModelCalls.ToString()));
    }

    /// <summary>
    /// Feature 8.9 - launching the browser is attempted once at startup as a best-effort
    /// convenience. If the underlying OS call fails, that failure is surfaced to the terminal
    /// rather than swallowed. The application has no visibility into, and makes no attempt to
    /// track, whether a window actually opened - the server is listening and idling regardless.
    /// </summary>
    private static void LaunchBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Emit.To(
                Surface.Terminal,
                EventKinds.BrowserLaunch,
                $"Asked the OS to open {url}. Whether a window appeared is not something this application tracks.");
        }
        catch (Exception ex)
        {
            Emit.To(
                Surface.Terminal,
                EventKinds.BrowserLaunch,
                $"Could not launch a browser: {ex.Message}\nOpen {url} yourself; the server is already listening.",
                Emit.Fields("exception", ex.GetType().Name));
        }
    }

    /// <summary>
    /// The prompts directory is the one configured path that must already exist — the project
    /// directory is created on demand, because an empty project is a valid startup state
    /// (feature 1.12), but templates are authored, not generated.
    ///
    /// Relative paths resolve against the working directory when no config file is in play, so
    /// the halt names the working directory too: "the directory does not exist" is a fact the
    /// operator can already see, whereas "and here is where I was standing when I looked" is the
    /// part that explains it.
    /// </summary>
    private static void RequirePromptsDirectory(AppConfig config)
    {
        if (Directory.Exists(config.PromptsDirectory))
        {
            return;
        }

        throw new HaltException(
            $"The prompts directory does not exist: {config.PromptsDirectory}\n" +
            $"  Working directory: {Environment.CurrentDirectory}\n" +
            "\n" +
            "Templates live beside the code that names them, at src/SpecRunner/prompts. Relative\n" +
            "paths resolve against the config file's directory, or against the working directory\n" +
            "when no config file is in use. Either:\n" +
            "  - run from the repository root, or\n" +
            "  - pass --config <path> to a JSON file naming prompts_directory explicitly.");
    }

    private static string Elide(string secret)
        => secret.Length == 0 ? "(not set)" : $"(elided, {secret.Length} characters)";
}

/// <summary>The three things the command line can say. Everything else is configuration.</summary>
internal sealed record CommandLineOptions(string? ConfigPath, bool ClearStaleLock, bool ShowHelp);

internal static class CommandLine
{
    public const string HelpText =
        """
        Spec Runner — a spec-driven development methodology, run from beginning to end.

          dotnet run --project src/SpecRunner [options]

        Options:
          --config <path>       Path to a JSON config file. Defaults to specrunner.config.json
                                beside the executable, or built-in defaults if that is absent.
          --clear-stale-lock    Proceed past a lock file whose process is no longer alive.
                                Required explicitly: clearing it automatically would be a guess.
          --help                This text.

        Configuration keys (all optional; relative paths resolve against the config file):
          project_directory     Where artifacts, records, runs and state live. Default: ./project
          prompts_directory     Where prompt templates live.   Default: ./src/SpecRunner/prompts
          base_url              OpenAI-compatible endpoint. Default: https://api.openai.com/v1
          port                  Fixed console port. Default: 5099
          api_key               Prefer the SPECRUNNER_API_KEY environment variable.
        """;

    public static CommandLineOptions Parse(string[] args)
    {
        string? configPath = null;
        var clearStaleLock = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (i + 1 >= args.Length)
                    {
                        throw new HaltException("--config requires a path.");
                    }

                    configPath = args[++i];
                    break;

                case "--clear-stale-lock":
                    clearStaleLock = true;
                    break;

                case "--help":
                case "-h":
                    showHelp = true;
                    break;

                default:
                    throw new HaltException($"Unknown argument '{args[i]}'. Run with --help.");
            }
        }

        return new CommandLineOptions(configPath, clearStaleLock, showHelp);
    }
}
