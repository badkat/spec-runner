using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Records;

namespace SpecRunner.Llm;

/// <summary>One attempt at one call, recorded whether or not it succeeded (feature 3.5).</summary>
public sealed record AttemptInfo(
    int Number,
    string Status,
    int? StatusCode,
    long LatencyMs,
    string ErrorBody,
    int BackoffMs);

/// <summary>Everything an artifact's origin header needs to explain where its body came from.</summary>
public sealed record LlmCallResult(
    string Content,
    string ModelReported,
    string ResponseId,
    string SystemFingerprint,
    string FinishReason,
    string PromptTokens,
    string CompletionTokens,
    string TotalTokens,
    string RequestRecordPath,
    string ResponseRecordPath,
    IReadOnlyList<AttemptInfo> Attempts);

/// <summary>
/// The only place in this application that talks to a model.
///
/// The model is a stateless text transformer reached over an OpenAI-compatible HTTP API. It has
/// no tools, no functions, and no influence over control flow - and that is asserted at runtime
/// in both directions rather than merely respected by convention (feature 3.3). Everything about
/// the call comes from the template's front matter (feature 3.1); this class contributes no
/// defaults of its own beyond the transport-level retry policy, which is a code constant.
/// </summary>
public sealed class LlmClient(HttpClient http, string baseUrl, string apiKey, RecordStore records)
{
    /// <summary>
    /// Feature 3.4 - retries exist only for transport-level failure and are byte-identical
    /// resends. These are code constants, not configuration: a new configuration surface for
    /// retry behaviour would be complexity in anticipation of a requirement that does not exist
    /// (Pillar 1).
    /// </summary>
    private const int MaxAttempts = 4;

    private static readonly int[] BackoffMs = [1000, 2000, 4000];

    public string Endpoint { get; } = baseUrl.TrimEnd('/') + "/chat/completions";

    public LlmCallResult Call(string stepId, string? iterationTarget, ResolvedPrompt prompt, CancellationToken cancellation)
    {
        var payload = BuildPayload(prompt);
        AssertNoToolSurfaceOutbound(payload, prompt.TemplatePath);
        var payloadJson = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        // Feature 3.6 - the request record is written before the request is sent, so a hard-kill
        // mid-call leaves a visible "initiated, never completed" record with the exact payload
        // rather than an invisible gap.
        var requestRecordPath = WriteRequestRecord(stepId, iterationTarget, prompt, payloadJson);

        Emit.To(
            Surface.Console,
            EventKinds.LlmRequest,
            $"Request written before sending: {requestRecordPath}",
            Emit.Fields(
                "step", stepId,
                "model", prompt.Config.Model,
                "temperature", PromptTemplate.Format(prompt.Config.Temperature),
                "top_p", PromptTemplate.Format(prompt.Config.TopP),
                "max_tokens", prompt.Config.MaxTokens.ToString(),
                "seed", prompt.Config.Seed.ToString(),
                "timeout_seconds", prompt.Config.TimeoutSeconds.ToString(),
                "resolved_prompt_hash", prompt.TextHash));

        var attempts = new List<AttemptInfo>();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage? response = null;

            try
            {
                using var perAttemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

                // Feature 3.8 - the timeout comes from front matter, and it is the same timeout on
                // every attempt. There is no escalating-timeout retry.
                perAttemptTimeout.CancelAfter(TimeSpan.FromSeconds(prompt.Config.TimeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (apiKey.Length > 0)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, perAttemptTimeout.Token);

                if (IsRetryableStatus(response.StatusCode))
                {
                    var statusCode = (int)response.StatusCode;
                    var body = ReadBodySafely(response);
                    stopwatch.Stop();
                    var backoff = RecordAttempt(
                        stepId, iterationTarget, attempts, attempt, "transport-failure",
                        statusCode, stopwatch.ElapsedMilliseconds, body);
                    response.Dispose();
                    response = null;

                    if (attempt == MaxAttempts)
                    {
                        throw new HaltException(
                            $"Retries exhausted after {MaxAttempts} attempts against {Endpoint} " +
                            $"(last status {statusCode}). Retry exhaustion is a halt (feature 3.5); " +
                            $"every attempt is recorded under {records.Paths.RecordDirectory(stepId, iterationTarget)}.");
                    }

                    Sleep(backoff, cancellation);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = ReadBodySafely(response);
                    stopwatch.Stop();
                    RecordAttempt(
                        stepId, iterationTarget, attempts, attempt, "rejected",
                        (int)response.StatusCode, stopwatch.ElapsedMilliseconds, body);

                    throw new HaltException(
                        $"The endpoint rejected the request with {(int)response.StatusCode} {response.StatusCode}. " +
                        "This is not a transport failure and is never retried (feature 3.4).\n" +
                        $"Response body:\n{body}");
                }

                var raw = ReadEventStream(response, prompt, cancellation, out var streamedDisplayText);
                stopwatch.Stop();
                RecordAttempt(
                    stepId, iterationTarget, attempts, attempt, "completed",
                    (int)response.StatusCode, stopwatch.ElapsedMilliseconds, "");

                var responseRecordPath = WriteResponseRecord(stepId, iterationTarget, raw, prompt);
                var assembled = AssembleAndAccept(raw, responseRecordPath, prompt);

                // Feature 8.5 - the streamed text is display only; the artifact is built from the
                // assembled final response, and the two are compared as a truncation check.
                if (assembled.Content != streamedDisplayText)
                {
                    throw new HaltException(
                        "The text streamed to the console and the text assembled from the recorded response " +
                        $"differ ({streamedDisplayText.Length} vs {assembled.Content.Length} characters). " +
                        "One of them is truncated, and the application will not choose which to believe. " +
                        $"Raw response: {responseRecordPath}");
                }

                Emit.To(
                    Surface.Console,
                    EventKinds.LlmResponse,
                    assembled.Content,
                    Emit.Fields(
                        "step", stepId,
                        "finish_reason", assembled.FinishReason,
                        "model_reported", assembled.ModelReported,
                        "response_id", assembled.ResponseId,
                        "system_fingerprint", assembled.SystemFingerprint,
                        "usage_total_tokens", assembled.TotalTokens,
                        "latency_ms", stopwatch.ElapsedMilliseconds.ToString(),
                        "raw_response_record", responseRecordPath));

                // Feature 3.7 - a server-reported model that differs from the requested string is
                // surfaced in the console as a named condition. It is not an error; it is a fact
                // about determinism that the operator must not have to go looking for.
                if (assembled.ModelReported != prompt.Config.Model)
                {
                    Emit.To(
                        Surface.Console,
                        EventKinds.LlmCondition,
                        $"The server reported model '{assembled.ModelReported}' for a request that asked for " +
                        $"'{prompt.Config.Model}'. Artifacts produced from this call record both.",
                        Emit.Fields("step", stepId, "requested", prompt.Config.Model, "reported", assembled.ModelReported));
                }

                return assembled with
                {
                    RequestRecordPath = requestRecordPath,
                    ResponseRecordPath = responseRecordPath,
                    Attempts = attempts
                };
            }
            catch (HaltException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                stopwatch.Stop();
                var kind = ex is OperationCanceledException ? "timeout" : "transport-failure";
                var backoff = RecordAttempt(
                    stepId, iterationTarget, attempts, attempt, kind,
                    null, stopwatch.ElapsedMilliseconds, ex.Message);

                if (attempt == MaxAttempts)
                {
                    throw new HaltException(
                        $"Retries exhausted after {MaxAttempts} attempts against {Endpoint}: {ex.Message}. " +
                        "Retry exhaustion is a halt (feature 3.5); every attempt is recorded on disk.", ex);
                }

                Sleep(backoff, cancellation);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw new HaltException("Unreachable: the retry loop exited without returning or halting.");
    }

    // ---- request construction -----------------------------------------------------------

    private static JsonObject BuildPayload(ResolvedPrompt prompt)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = prompt.Text
            }
        };

        return new JsonObject
        {
            ["model"] = prompt.Config.Model,
            ["messages"] = messages,
            ["temperature"] = prompt.Config.Temperature,
            ["top_p"] = prompt.Config.TopP,
            ["max_tokens"] = prompt.Config.MaxTokens,
            ["seed"] = prompt.Config.Seed,
            ["n"] = 1,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
    }

    /// <summary>
    /// Feature 3.3 - outgoing requests are asserted to contain no tools, functions, or
    /// tool_choice fields, and n: 1. Pillar 4 is enforced by the code at runtime, not merely
    /// respected by the person who wrote BuildPayload.
    /// </summary>
    private static void AssertNoToolSurfaceOutbound(JsonObject payload, string templatePath)
    {
        foreach (var forbidden in new[] { "tools", "functions", "tool_choice", "function_call" })
        {
            if (payload.ContainsKey(forbidden))
            {
                throw new HaltException(
                    $"The outgoing request for {templatePath} contains a '{forbidden}' field. " +
                    "This application gives the model no mechanism to invoke behaviour (Pillar 4); " +
                    "a tool surface in an outgoing payload is a violated invariant, not a feature.");
            }
        }

        if (payload["n"]?.GetValue<int>() != 1)
        {
            throw new HaltException($"The outgoing request for {templatePath} does not set n: 1.");
        }
    }

    // ---- response reading ---------------------------------------------------------------

    private static string ReadEventStream(
        HttpResponseMessage response,
        ResolvedPrompt prompt,
        CancellationToken cancellation,
        out string streamedDisplayText)
    {
        var raw = new StringBuilder();
        var displayed = new StringBuilder();

        using var stream = response.Content.ReadAsStream(cancellation);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested();
            raw.Append(line).Append('\n');

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            // Feature 8.5 - model output streams token-by-token to the console. A ninety-second
            // step with no output is indistinguishable from a hang.
            var delta = ExtractDeltaContent(data);
            if (delta.Length == 0)
            {
                continue;
            }

            displayed.Append(delta);
            Emit.To(
                Surface.Console,
                EventKinds.LlmToken,
                delta,
                Emit.Fields("model", prompt.Config.Model),
                transientDisplayOnly: true);
        }

        streamedDisplayText = Canonical.Text(displayed.ToString());
        return raw.ToString();
    }

    private static string ExtractDeltaContent(string data)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new HaltException($"A stream chunk was not valid JSON: {data}", ex);
        }

        var choices = node?["choices"] as JsonArray;
        if (choices is null || choices.Count == 0)
        {
            return "";
        }

        return choices[0]?["delta"]?["content"]?.GetValue<string>() ?? "";
    }

    /// <summary>
    /// Feature 3.2 - response acceptance is a closed whitelist. Accept exactly
    /// <c>finish_reason: stop</c> with non-empty content. <c>length</c> is a halt, not a
    /// truncated artifact. Refusal, content filter, empty content, multiple choices, or an
    /// absent choices array are halts. Never salvage.
    ///
    /// The content is assembled here from the recorded raw stream, independently of the text that
    /// was displayed while it arrived, so that the two can be compared (feature 8.5).
    /// </summary>
    private static LlmCallResult AssembleAndAccept(string raw, string responseRecordPath, ResolvedPrompt prompt)
    {
        var content = new StringBuilder();
        var finishReasons = new List<string>();
        string? model = null;
        string? responseId = null;
        string? fingerprint = null;
        string promptTokens = "-", completionTokens = "-", totalTokens = "-";
        var sawChoice = false;
        var sawAnyChunk = false;

        foreach (var line in raw.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            sawAnyChunk = true;
            var node = JsonNode.Parse(data)
                ?? throw new HaltException($"A stream chunk parsed to null JSON. Raw response: {responseRecordPath}");

            model ??= node["model"]?.GetValue<string>();
            responseId ??= node["id"]?.GetValue<string>();
            fingerprint ??= node["system_fingerprint"]?.GetValue<string>();

            if (node["usage"] is JsonObject usage)
            {
                promptTokens = usage["prompt_tokens"]?.ToJsonString() ?? "-";
                completionTokens = usage["completion_tokens"]?.ToJsonString() ?? "-";
                totalTokens = usage["total_tokens"]?.ToJsonString() ?? "-";
            }

            if (node["error"] is not null)
            {
                throw new HaltException(
                    $"The stream carried an error object: {node["error"]!.ToJsonString()}. " +
                    $"Raw response: {responseRecordPath}");
            }

            if (node["choices"] is not JsonArray choices)
            {
                // The only chunk permitted to lack a choices array is the usage chunk this
                // application explicitly asked for via stream_options.include_usage. Anything
                // else with no choices is the "absent choices array" 3.2 names as a halt.
                if (node["usage"] is not null)
                {
                    continue;
                }

                throw new HaltException(
                    $"A stream chunk has no 'choices' array and carries no usage object. " +
                    $"Raw response: {responseRecordPath}\nChunk: {data}");
            }

            if (choices.Count == 0)
            {
                if (node["usage"] is not null)
                {
                    continue;
                }

                throw new HaltException(
                    $"A stream chunk has an empty 'choices' array and carries no usage object. " +
                    $"Raw response: {responseRecordPath}\nChunk: {data}");
            }

            if (choices.Count > 1)
            {
                throw new HaltException(
                    $"The response carried {choices.Count} choices; this application asked for exactly one " +
                    $"and accepts exactly one (feature 3.2). Raw response: {responseRecordPath}");
            }

            sawChoice = true;
            var choice = choices[0]!;

            if (choice["index"]?.GetValue<int>() is int index && index != 0)
            {
                throw new HaltException(
                    $"A stream chunk reported choice index {index}; only index 0 is accepted. " +
                    $"Raw response: {responseRecordPath}");
            }

            // Feature 3.3 - an incoming response containing tool_calls is a hard halt with the raw
            // body preserved, treated as a violated invariant rather than an unsupported feature.
            if (choice["delta"]?["tool_calls"] is not null || choice["message"]?["tool_calls"] is not null)
            {
                throw new HaltException(
                    "The response contains tool_calls. This application sends no tool surface and the model " +
                    "has no mechanism to invoke behaviour (Pillar 4); receiving tool_calls means an invariant " +
                    $"has been violated somewhere. The raw body is preserved at {responseRecordPath}.");
            }

            if (choice["delta"]?["refusal"] is { } refusal && refusal.GetValue<string>().Length > 0)
            {
                throw new HaltException(
                    $"The model refused: {refusal.GetValue<string>()}. A refusal is a halt, never salvaged " +
                    $"(feature 3.2). Raw response: {responseRecordPath}");
            }

            content.Append(choice["delta"]?["content"]?.GetValue<string>() ?? "");

            if (choice["finish_reason"] is { } reason && reason.GetValueKind() != JsonValueKind.Null)
            {
                finishReasons.Add(reason.GetValue<string>());
            }
        }

        if (!sawAnyChunk)
        {
            throw new HaltException($"The response stream contained no data chunks. Raw response: {responseRecordPath}");
        }

        if (!sawChoice)
        {
            throw new HaltException($"The response stream contained no choice. Raw response: {responseRecordPath}");
        }

        if (finishReasons.Count != 1)
        {
            throw new HaltException(
                $"The response reported {finishReasons.Count} finish reasons ({string.Join(", ", finishReasons)}); " +
                $"exactly one is accepted. Raw response: {responseRecordPath}");
        }

        var finishReason = finishReasons[0];
        if (finishReason != "stop")
        {
            var note = finishReason switch
            {
                "length" => $"The response hit max_tokens ({prompt.Config.MaxTokens} in {prompt.TemplatePath}). " +
                            "A truncated artifact would be worse than no artifact, so this is a halt.",
                "content_filter" => "The response was filtered by the provider.",
                _ => "Only finish_reason 'stop' is accepted (feature 3.2)."
            };

            throw new HaltException(
                $"finish_reason was '{finishReason}', not 'stop'. {note} Raw response: {responseRecordPath}");
        }

        var assembled = Canonical.Text(content.ToString());
        if (assembled.Trim().Length == 0)
        {
            throw new HaltException($"The response completed with empty content. Raw response: {responseRecordPath}");
        }

        return new LlmCallResult(
            assembled,
            model ?? "-",
            responseId ?? "-",
            fingerprint ?? "-",
            finishReason,
            promptTokens,
            completionTokens,
            totalTokens,
            "-",
            responseRecordPath,
            []);
    }

    // ---- records --------------------------------------------------------------------------

    /// <summary>
    /// Feature 2.3 - the exact model-facing request payload: model, messages, sampling
    /// parameters. Never transport or auth details, which are configuration and not call content.
    /// </summary>
    private string WriteRequestRecord(string stepId, string? target, ResolvedPrompt prompt, string payloadJson)
    {
        var (block, fenceLength) = Fence.Wrap(payloadJson, "json");
        return records.WriteAuxiliary(stepId, target, RecordStore.LlmRequestKind, "", doc =>
        {
            doc.Set("template_path", prompt.TemplatePath)
               .Set("template_hash", prompt.TemplateHash)
               .Set("resolved_prompt_hash", prompt.TextHash)
               .Set("model_requested", prompt.Config.Model)
               .Set("parser", prompt.Config.Parser)
               .Set("fence_length", fenceLength)
               .SetMapList("substituted_values", prompt.Values.Select(v => new List<KeyValuePair<string, string>>
               {
                   new("name", v.Name),
                   new("hash", v.Hash),
                   new("source", v.Source)
               }));

            doc.PreserveBodyVerbatim = true;
            doc.Body =
                "Written before the request was sent. If no matching llm-response record exists beside this\n"
                + "one, the run was interrupted mid-call and this is the exact payload that was in flight.\n\n"
                + "## Model-facing payload\n\n"
                + block + "\n";
        });
    }

    private string WriteResponseRecord(string stepId, string? target, string raw, ResolvedPrompt prompt)
    {
        var (block, fenceLength) = Fence.Wrap(raw, "");
        return records.WriteAuxiliary(stepId, target, RecordStore.LlmResponseKind, "", doc =>
        {
            doc.Set("template_path", prompt.TemplatePath)
               .Set("resolved_prompt_hash", prompt.TextHash)
               .Set("transfer_encoding", "text/event-stream")
               .Set("fence_length", fenceLength);

            doc.PreserveBodyVerbatim = true;
            doc.Body =
                "The exact response body received, byte for byte. Parsing and normalisation happen\n"
                + "elsewhere and are never destructive of this record (feature 2.3).\n\n"
                + "## Raw response\n\n"
                + block + "\n";
        });
    }

    private int RecordAttempt(
        string stepId,
        string? target,
        List<AttemptInfo> attempts,
        int number,
        string status,
        int? statusCode,
        long latencyMs,
        string errorBody)
    {
        var backoff = status == "completed" || number >= MaxAttempts
            ? 0
            : BackoffMs[Math.Min(number - 1, BackoffMs.Length - 1)];

        var info = new AttemptInfo(number, status, statusCode, latencyMs, errorBody, backoff);
        attempts.Add(info);

        records.WriteAuxiliary(stepId, target, RecordStore.LlmAttemptKind, $".a{number}", doc =>
        {
            doc.Set("attempt", number)
               .Set("status", status)
               .Set("status_code", statusCode?.ToString(CultureInfo.InvariantCulture) ?? "-")
               .Set("latency_ms", latencyMs.ToString(CultureInfo.InvariantCulture))
               .Set("backoff_ms", backoff)
               .Set("max_attempts", MaxAttempts);
            doc.Body = errorBody.Length == 0 ? "No error body." : "## Error body\n\n" + Fence.Wrap(errorBody, "").Block;
        });

        // Feature 3.5 - every attempt is recorded *and streamed*. A retry the operator cannot see
        // is silent recovery, which Pillar 3 rejects outright.
        Emit.To(
            Surface.Console,
            EventKinds.LlmAttempt,
            errorBody.Length == 0 ? $"Attempt {number}/{MaxAttempts}: {status}" : $"Attempt {number}/{MaxAttempts}: {status} - {Truncate(errorBody)}",
            Emit.Fields(
                "step", stepId,
                "attempt", number.ToString(),
                "status", status,
                "status_code", statusCode?.ToString(CultureInfo.InvariantCulture) ?? "-",
                "latency_ms", latencyMs.ToString(CultureInfo.InvariantCulture),
                "backoff_ms", backoff.ToString(CultureInfo.InvariantCulture)));

        return backoff;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static bool IsRetryableStatus(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static string ReadBodySafely(HttpResponseMessage response)
    {
        try
        {
            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"(the response body could not be read: {ex.Message})";
        }
    }

    private static void Sleep(int milliseconds, CancellationToken cancellation)
    {
        if (milliseconds > 0)
        {
            cancellation.WaitHandle.WaitOne(milliseconds);
        }
    }

    private static string Truncate(string text)
        => text.Length <= 300 ? text.Replace('\n', ' ') : text[..300].Replace('\n', ' ') + "...";
}
