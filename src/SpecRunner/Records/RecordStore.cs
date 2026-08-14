using SpecRunner.Core;

namespace SpecRunner.Records;

/// <summary>
/// Feature 2.6 - one record file per step under a directory tree mirroring step ids. Not a
/// single growing journal, not a database. The operator navigates records the same way they
/// navigate the workflow, and every file here opens in an editor as Markdown.
///
/// Nothing in this class deletes or overwrites. Superseding is additive (feature 1.7): a new
/// completion record lands beside the old one, and an invalidation record names which one it
/// retired. "In force" is therefore computed by reading, never stored as a mutable flag.
/// </summary>
public sealed class RecordStore(ProjectPaths paths, RunIdentity run)
{
    /// <summary>Written in place of an absent iteration target so every record has a value there.</summary>
    public const string NoTarget = "-";

    public const string CompletionKind = "completion";
    public const string InvalidationKind = "invalidation";
    public const string QuestionKind = "question";
    public const string AnswerKind = "answer";
    public const string DecisionKind = "decision";
    public const string LlmRequestKind = "llm-request";
    public const string LlmAttemptKind = "llm-attempt";
    public const string LlmResponseKind = "llm-response";
    public const string RunOutcomeKind = "run-outcome";

    public ProjectPaths Paths { get; } = paths;

    public RunIdentity Run { get; } = run;

    // ---- writing -----------------------------------------------------------------------

    public CompletionRecord WriteCompletion(
        string stepId,
        string? target,
        IReadOnlyList<InputRef> inputs,
        IReadOnlyList<OutputVariable> outputVariables,
        IReadOnlyList<ArtifactRef> outputArtifacts,
        string summary)
    {
        var sequence = Run.NextSequence();
        var timestamp = RunIdentity.TimestampUtc();
        var doc = BaseDoc(CompletionKind, stepId, target, sequence, timestamp);

        doc.SetMapList("inputs", inputs.Select(i => i.ToRow()));
        doc.SetMapList("output_variables", outputVariables.Select(v => v.ToRow()));
        doc.SetMapList("output_artifacts", outputArtifacts.Select(a => a.ToRow()));
        doc.Body = summary;

        var path = Path.Combine(Paths.RecordDirectory(stepId, target), $"{CompletionKind}.{Run.Id}.s{sequence:D5}.md");
        AtomicFile.WriteNew(path, doc.Serialize());

        return new CompletionRecord(
            Paths.Relative(path), stepId, target, Run.Id, sequence, timestamp,
            Canonical.Version, inputs, outputVariables, outputArtifacts);
    }

    /// <summary>
    /// Feature 1.7 - invalidating writes a record naming the target, the cause, the specific
    /// input that differed, both hashes, and the run id. The superseded completion record stays
    /// where it is.
    /// </summary>
    public InvalidationRecord WriteInvalidation(
        string stepId,
        string? target,
        string targetRecordPath,
        string cause,
        string differingInput,
        string expectedHash,
        string actualHash,
        string raisedByStep,
        string explanation)
    {
        if (!InvalidationCause.All.Contains(cause))
        {
            throw new HaltException($"Unknown invalidation cause '{cause}'. The cause taxonomy is closed (feature 1.7).");
        }

        var sequence = Run.NextSequence();
        var timestamp = RunIdentity.TimestampUtc();
        var doc = BaseDoc(InvalidationKind, stepId, target, sequence, timestamp);

        doc.Set("target_record", targetRecordPath)
           .Set("cause", cause)
           .Set("differing_input", differingInput)
           .Set("expected_hash", expectedHash)
           .Set("actual_hash", actualHash)
           .Set("raised_by_step", raisedByStep);
        doc.Body = explanation;

        var path = Path.Combine(Paths.RecordDirectory(stepId, target), $"{InvalidationKind}.{Run.Id}.s{sequence:D5}.md");
        AtomicFile.WriteNew(path, doc.Serialize());

        return new InvalidationRecord(
            Paths.Relative(path), stepId, target, targetRecordPath, cause, differingInput,
            expectedHash, actualHash, raisedByStep, Run.Id, sequence, timestamp);
    }

    public QuestionRecord WriteQuestion(
        string stepId,
        string? target,
        int version,
        string question,
        IReadOnlyList<string> permittedAnswers,
        string codeLocation,
        IReadOnlyList<InputRef> inputs,
        IReadOnlyList<string> relatedArtifacts)
    {
        var sequence = Run.NextSequence();
        var timestamp = RunIdentity.TimestampUtc();
        var doc = BaseDoc(QuestionKind, stepId, target, sequence, timestamp);

        doc.Set("question_version", version)
           .Set("question", question)
           .SetList("permitted_answers", permittedAnswers)
           .Set("code_location", codeLocation)
           .SetMapList("inputs", inputs.Select(i => i.ToRow()))
           .SetList("related_artifacts", relatedArtifacts);

        var answerFile = AnswerFileName(version);
        doc.Body =
            $"""
             ## {question}

             The workflow has stopped here and will not choose for you (Pillar 8).

             Answer from the browser console, or by hand: create `{answerFile}` in this same
             directory with front matter exactly as shown, and nothing else changed.

             ```
             ---
             record_kind: answer
             step_id: {stepId}
             iteration_target: {target ?? NoTarget}
             answer_version: {version}
             answer: <one of: {string.Join(" | ", permittedAnswers)}>
             answer_surface: {AnswerRecord.SurfaceHandWritten}
             timestamp_utc: <UTC ISO-8601, e.g. {timestamp}>
             ---
             ```

             An answer outside the permitted set is a halt, not a re-ask. Once answered, this
             decision is in force and immutable: changing it means invalidating it first, from
             the console or by deleting this step's completion record, which cascades to
             everything derived from it (feature 6.3).

             ### Inputs that produced this question

             {(inputs.Count == 0 ? "_none declared_" : string.Join("\n", inputs.Select(i => $"- `{i.Kind}` **{i.Name}** — `{i.Hash}` (from {i.Producer})")))}

             ### Artifacts in play

             {(relatedArtifacts.Count == 0 ? "_none_" : string.Join("\n", relatedArtifacts.Select(a => $"- `{a}`")))}

             Raised by `{codeLocation}`.
             """;

        var path = Path.Combine(Paths.RecordDirectory(stepId, target), QuestionFileName(version));
        AtomicFile.WriteNew(path, doc.Serialize());

        return new QuestionRecord(
            Paths.Relative(path), stepId, target, version, question, permittedAnswers,
            codeLocation, inputs, relatedArtifacts, Run.Id, sequence, timestamp);
    }

    /// <summary>
    /// Feature 6.1 - a browser click is simply the server performing, on the operator's behalf,
    /// the same write their editor would have performed. Same file, same mechanism, same shape;
    /// only the recorded surface marker differs.
    /// </summary>
    public AnswerRecord WriteAnswer(string stepId, string? target, int version, string answer, string surface)
    {
        var timestamp = RunIdentity.TimestampUtc();
        var doc = new MdDoc()
            .Set("record_kind", AnswerKind)
            .Set("step_id", stepId)
            .Set("iteration_target", target ?? NoTarget)
            .Set("answer_version", version)
            .Set("answer", answer)
            .Set("answer_surface", surface)
            .Set("timestamp_utc", timestamp);

        var path = Path.Combine(Paths.RecordDirectory(stepId, target), AnswerFileName(version));
        AtomicFile.WriteNew(path, doc.Serialize());
        return new AnswerRecord(Paths.Relative(path), stepId, target, version, answer, surface, timestamp);
    }

    /// <summary>
    /// Feature 2.8 - every point where code selects a path writes a decision record: the
    /// predicate's identity, the exact input value it evaluated, the enumerated options, the
    /// chosen branch, and the step that follows. Control flow itself lands on disk, which is
    /// what makes Pillar 4's test - re-derive the branch from code and stored inputs alone -
    /// actually satisfiable.
    /// </summary>
    public string WriteDecision(
        string stepId,
        string? target,
        string predicateIdentity,
        string evaluatedInput,
        string evaluatedValue,
        IReadOnlyList<string> options,
        string chosenBranch,
        string nextStep,
        string explanation)
    {
        var sequence = Run.NextSequence();
        var timestamp = RunIdentity.TimestampUtc();
        var doc = BaseDoc(DecisionKind, stepId, target, sequence, timestamp);

        doc.Set("predicate", predicateIdentity)
           .Set("evaluated_input", evaluatedInput)
           .Set("evaluated_value", evaluatedValue)
           .SetList("options", options)
           .Set("chosen_branch", chosenBranch)
           .Set("next_step", nextStep);
        doc.Body = explanation;

        var path = Path.Combine(Paths.RecordDirectory(stepId, target), $"{DecisionKind}.{Run.Id}.s{sequence:D5}.md");
        AtomicFile.WriteNew(path, doc.Serialize());
        return Paths.Relative(path);
    }

    /// <summary>Writes an arbitrary record file for a step, used by the LLM call records.</summary>
    public string WriteAuxiliary(string stepId, string? target, string kind, string fileSuffix, Action<MdDoc> fill)
    {
        var sequence = Run.NextSequence();
        var doc = BaseDoc(kind, stepId, target, sequence, RunIdentity.TimestampUtc());
        fill(doc);

        var path = Path.Combine(
            Paths.RecordDirectory(stepId, target),
            $"{kind}.{Run.Id}.s{sequence:D5}{fileSuffix}.md");
        AtomicFile.WriteNew(path, doc.Serialize());
        return Paths.Relative(path);
    }

    /// <summary>
    /// Feature 9.3 / 9.5 - how the run ended, written where the next run's pre-flight will find
    /// it and where a person reading the project tree can see it without the application running.
    /// </summary>
    public string WriteRunOutcome(string outcome, string detail, string? stepId, string? target)
    {
        var sequence = Run.NextSequence();
        var doc = new MdDoc()
            .Set("record_kind", RunOutcomeKind)
            .Set("outcome", outcome)
            .Set("run_id", Run.Id)
            .Set("sequence", sequence)
            .Set("timestamp_utc", RunIdentity.TimestampUtc())
            .Set("step_id", stepId ?? NoTarget)
            .Set("iteration_target", target ?? NoTarget);
        doc.Body = detail;

        Directory.CreateDirectory(Paths.Runs);
        var path = Path.Combine(Paths.Runs, $"{Run.Id}.outcome.md");
        if (File.Exists(path))
        {
            // A run ends once. If an outcome already exists, the second one is itself the news.
            path = Path.Combine(Paths.Runs, $"{Run.Id}.outcome.s{sequence:D5}.md");
        }

        AtomicFile.WriteNew(path, doc.Serialize());
        return Paths.Relative(path);
    }

    // ---- reading -----------------------------------------------------------------------

    public static string QuestionFileName(int version) => $"{QuestionKind}.v{version}.md";

    public static string AnswerFileName(int version) => $"{AnswerKind}.v{version}.md";

    /// <summary>All completion records for a step (and target), oldest first.</summary>
    public IReadOnlyList<CompletionRecord> ReadCompletions(string stepId, string? target)
    {
        var directory = Paths.RecordDirectory(stepId, target);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var records = new List<CompletionRecord>();
        foreach (var file in Directory.GetFiles(directory, $"{CompletionKind}.*.md"))
        {
            records.Add(ParseCompletion(file));
        }

        return [.. records.OrderBy(r => r.Order.Item1, StringComparer.Ordinal).ThenBy(r => r.Order.Item2)];
    }

    public IReadOnlyList<InvalidationRecord> ReadInvalidations(string stepId, string? target)
    {
        var directory = Paths.RecordDirectory(stepId, target);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var records = new List<InvalidationRecord>();
        foreach (var file in Directory.GetFiles(directory, $"{InvalidationKind}.*.md"))
        {
            records.Add(ParseInvalidation(file));
        }

        return [.. records.OrderBy(r => r.RunId, StringComparer.Ordinal).ThenBy(r => r.Sequence)];
    }

    /// <summary>
    /// The completion record currently in force: the most recent one that no invalidation record
    /// names. Feature 1.9 - a missing record means not-done; a malformed record is a halt, which
    /// the parsers below deliver.
    /// </summary>
    public CompletionRecord? InForceCompletion(string stepId, string? target)
    {
        var completions = ReadCompletions(stepId, target);
        if (completions.Count == 0)
        {
            return null;
        }

        var retired = ReadInvalidations(stepId, target).Select(i => i.TargetRecordPath).ToHashSet(StringComparer.Ordinal);
        return completions.LastOrDefault(c => !retired.Contains(c.Path));
    }

    /// <summary>
    /// The current question round for a decision step. Rounds are versions: invalidating a
    /// decision opens round n+1, and every earlier round's question and answer stay on disk
    /// untouched (feature 6.3 - revision never edits history, it supersedes it).
    /// </summary>
    public int CurrentQuestionVersion(string stepId, string? target)
    {
        var directory = Paths.RecordDirectory(stepId, target);
        if (!Directory.Exists(directory))
        {
            return 1;
        }

        var highest = 0;
        foreach (var file in Directory.GetFiles(directory, $"{QuestionKind}.v*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name.AsSpan(QuestionKind.Length + 2), out var version) && version > highest)
            {
                highest = version;
            }
        }

        return highest == 0 ? 1 : highest;
    }

    public QuestionRecord? ReadQuestion(string stepId, string? target, int version)
    {
        var path = Path.Combine(Paths.RecordDirectory(stepId, target), QuestionFileName(version));
        if (!File.Exists(path))
        {
            return null;
        }

        var origin = Paths.Relative(path);
        var doc = MdDoc.Parse(File.ReadAllText(path), origin);
        return new QuestionRecord(
            origin,
            doc.Require("step_id", origin),
            NullableTarget(doc.Require("iteration_target", origin)),
            doc.RequireInt("question_version", origin),
            doc.Require("question", origin),
            doc.RequireList("permitted_answers", origin),
            doc.Require("code_location", origin),
            [.. doc.RequireMapList("inputs", origin).Select(r => InputRef.FromRow(r, origin))],
            doc.RequireList("related_artifacts", origin),
            doc.Require("run_id", origin),
            doc.RequireInt("sequence", origin),
            doc.Require("timestamp_utc", origin));
    }

    public AnswerRecord? ReadAnswer(string stepId, string? target, int version)
    {
        var path = Path.Combine(Paths.RecordDirectory(stepId, target), AnswerFileName(version));
        if (!File.Exists(path))
        {
            return null;
        }

        var origin = Paths.Relative(path);
        var doc = MdDoc.Parse(File.ReadAllText(path), origin);
        var record = new AnswerRecord(
            origin,
            doc.Require("step_id", origin),
            NullableTarget(doc.Require("iteration_target", origin)),
            doc.RequireInt("answer_version", origin),
            doc.Require("answer", origin),
            doc.Require("answer_surface", origin),
            doc.Require("timestamp_utc", origin));

        if (!AnswerRecord.Surfaces.Contains(record.Surface))
        {
            throw new HaltException(
                $"Malformed answer record {origin}: answer_surface is '{record.Surface}', " +
                $"which is not one of: {string.Join(", ", AnswerRecord.Surfaces)}. Feature 6.4 requires every " +
                "answer to say which channel produced it, and an unrecognised channel is a halt, not a guess.");
        }

        return record;
    }

    /// <summary>
    /// The iteration targets a step has records for, read from the per-target subdirectories on
    /// disk. Used when a step must be invalidated by cascade before its frozen iteration list is
    /// available to enumerate - the record tree still knows which items were run.
    /// </summary>
    public IReadOnlyList<string> RecordedTargets(string stepId)
    {
        var directory = Paths.RecordDirectory(stepId, null);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        // The directory name is the *sanitized* target, so the true identity is read back out of
        // a record inside it rather than reconstructed from the path.
        var targets = new List<string>();
        foreach (var child in Directory.GetDirectories(directory))
        {
            var completions = Directory.GetFiles(child, $"{CompletionKind}.*.md");
            if (completions.Length == 0)
            {
                continue;
            }

            var target = ParseCompletion(completions[0]).IterationTarget;
            if (target is not null && !targets.Contains(target))
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>
    /// Feature 3.6 - request records with no matching response record beside them. A hard-kill
    /// mid-call leaves a visible "initiated, never completed" record, and the next run reports it
    /// in the pre-flight plan rather than leaving an invisible gap.
    /// </summary>
    public IReadOnlyList<string> IncompleteModelCalls()
    {
        if (!Directory.Exists(Paths.Records))
        {
            return [];
        }

        var incomplete = new List<string>();
        foreach (var directory in Directory.GetDirectories(Paths.Records, "*", SearchOption.AllDirectories))
        {
            var requests = Directory.GetFiles(directory, $"{LlmRequestKind}.*.md");
            var responses = Directory.GetFiles(directory, $"{LlmResponseKind}.*.md");
            if (requests.Length <= responses.Length)
            {
                continue;
            }

            Array.Sort(requests, StringComparer.Ordinal);
            for (var i = responses.Length; i < requests.Length; i++)
            {
                incomplete.Add(Paths.Relative(requests[i]));
            }
        }

        return [.. incomplete.OrderBy(p => p, StringComparer.Ordinal)];
    }

    /// <summary>Every invalidation record anywhere in the project, for ping-pong detection (7.4).</summary>
    public IReadOnlyList<InvalidationRecord> ReadAllInvalidations()
    {
        if (!Directory.Exists(Paths.Records))
        {
            return [];
        }

        var records = new List<InvalidationRecord>();
        foreach (var file in Directory.GetFiles(Paths.Records, $"{InvalidationKind}.*.md", SearchOption.AllDirectories))
        {
            records.Add(ParseInvalidation(file));
        }

        return [.. records.OrderBy(r => r.RunId, StringComparer.Ordinal).ThenBy(r => r.Sequence)];
    }

    public CompletionRecord ParseCompletion(string file)
    {
        var origin = Paths.Relative(file);
        var doc = MdDoc.Parse(File.ReadAllText(file), origin);

        // The canonicalization version is read, not enforced, here. Feature 1.5 says a version
        // bump *invalidates* everything at once rather than halting - so the reconciler needs to
        // be able to parse a record written under older rules in order to write the invalidation
        // record that supersedes it.
        return new CompletionRecord(
            origin,
            doc.Require("step_id", origin),
            NullableTarget(doc.Require("iteration_target", origin)),
            doc.Require("run_id", origin),
            doc.RequireInt("sequence", origin),
            doc.Require("timestamp_utc", origin),
            doc.RequireInt("canonicalization_version", origin),
            [.. doc.RequireMapList("inputs", origin).Select(r => InputRef.FromRow(r, origin))],
            [.. doc.RequireMapList("output_variables", origin).Select(r => OutputVariable.FromRow(r, origin))],
            [.. doc.RequireMapList("output_artifacts", origin).Select(r => ArtifactRef.FromRow(r, origin))]);
    }

    private InvalidationRecord ParseInvalidation(string file)
    {
        var origin = Paths.Relative(file);
        var doc = MdDoc.Parse(File.ReadAllText(file), origin);

        return new InvalidationRecord(
            origin,
            doc.Require("step_id", origin),
            NullableTarget(doc.Require("iteration_target", origin)),
            doc.Require("target_record", origin),
            doc.Require("cause", origin),
            doc.Require("differing_input", origin),
            doc.Require("expected_hash", origin),
            doc.Require("actual_hash", origin),
            doc.Require("raised_by_step", origin),
            doc.Require("run_id", origin),
            doc.RequireInt("sequence", origin),
            doc.Require("timestamp_utc", origin));
    }

    public static string? NullableTarget(string value) => value == NoTarget ? null : value;

    private MdDoc BaseDoc(string kind, string stepId, string? target, int sequence, string timestamp)
        => new MdDoc()
            .Set("record_kind", kind)
            .Set("step_id", stepId)
            .Set("iteration_target", target ?? NoTarget)
            .Set("run_id", Run.Id)
            .Set("sequence", sequence)
            .Set("timestamp_utc", timestamp)
            .Set("hash_algorithm", Canonical.HashAlgorithm)
            .Set("canonicalization_version", Canonical.Version);
}
