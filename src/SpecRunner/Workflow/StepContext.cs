using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Engine;
using SpecRunner.Graph;
using SpecRunner.Llm;
using SpecRunner.Reconcile;
using SpecRunner.Records;
using SpecRunner.State;

namespace SpecRunner.Workflow;

/// <summary>Signalled when the operator stops the run at a boundary (feature 9.3).</summary>
public sealed class StopRequestedException() : Exception("The operator stopped the run.");

/// <summary>What a model call produced, before anything has been parsed out of it.</summary>
public sealed record ModelCall(LlmCallResult Result, ResolvedPrompt Prompt);

/// <summary>
/// Everything a step is allowed to touch, and nothing else.
///
/// The context is constructed by the runner with the step's declarations already resolved: the
/// state view is narrowed to declared reads, the inputs are already computed and hashed, and the
/// artifacts the step declared are already located. A step cannot reach around any of it.
/// </summary>
public sealed class StepContext
{
    private readonly DependencyGraph _graph;
    private readonly Reconciler _reconciler;
    private readonly ProjectState _state;
    private readonly ArtifactIndex _index;
    private readonly List<OutputVariable> _committedVariables = [];
    private readonly List<ArtifactRef> _writtenArtifacts = [];
    private readonly List<InputRef> _inputs;

    public StepContext(
        Step step,
        string? iterationTarget,
        IReadOnlyList<string> iterationOrder,
        DependencyGraph graph,
        Reconciler reconciler,
        ProjectState state,
        ArtifactIndex index,
        RecordStore records,
        ArtifactStore artifacts,
        LlmClient llm,
        RunControl control,
        ProjectPaths paths,
        string promptsDirectory,
        IReadOnlyList<InputRef> inputs,
        string supersedeReason,
        Action<string, string, string> confirmDefect)
    {
        ConfirmDefect = confirmDefect;
        Step = step;
        IterationTarget = iterationTarget;
        IterationOrder = iterationOrder;
        _graph = graph;
        _reconciler = reconciler;
        _state = state;
        _index = index;
        Records = records;
        Artifacts = artifacts;
        Llm = llm;
        Control = control;
        Paths = paths;
        PromptsDirectory = promptsDirectory;
        _inputs = [.. inputs];
        SupersedeReason = supersedeReason;
        State = new ScopedState(state, step.Id, step.ReadsVariables, iterationTarget, IterationOrderProducing);
    }

    public Step Step { get; }

    public string? IterationTarget { get; }

    public IReadOnlyList<string> IterationOrder { get; }

    public ScopedState State { get; }

    public RecordStore Records { get; }

    public ArtifactStore Artifacts { get; }

    public LlmClient Llm { get; }

    public RunControl Control { get; }

    public ProjectPaths Paths { get; }

    public string PromptsDirectory { get; }

    /// <summary>
    /// The declared inputs, already hashed, recorded verbatim in the completion record.
    ///
    /// One input cannot exist before the step runs: the answer to a question the step is about to
    /// ask. It is appended when the answer arrives (feature 6.3), in exactly the shape the
    /// reconciler will recompute on the next run.
    /// </summary>
    public IReadOnlyList<InputRef> Inputs => _inputs;

    /// <summary>
    /// Feature 7.5 - when this step is re-executing after an invalidation, the reason travels
    /// into the artifact it writes, so a new version records the version it supersedes and the
    /// finding that caused it. Version numbers alone do not explain why v4 exists.
    /// </summary>
    public string SupersedeReason { get; }

    /// <summary>
    /// Feature 7.3 - hands a confirmed defect to the ordinary invalidation cascade. The arguments
    /// are the upstream step id, the downstream step that raised the finding, and the finding
    /// artifact. There is no separate rewind engine on the other side of this call.
    /// </summary>
    public Action<string, string, string> ConfirmDefect { get; }

    /// <summary>
    /// The step that declares an artifact as its output. Used where a step must turn a parsed
    /// artifact id into the step whose record has to be invalidated - code resolving a value, not
    /// the model naming a step.
    /// </summary>
    public string ProducerOf(string artifactId)
    {
        if (_graph.ArtifactProducers.TryGetValue(artifactId, out var producer))
        {
            return producer;
        }

        throw new HaltException(
            $"No step declares artifact '{artifactId}' as an output. " +
            $"Known artifacts: {string.Join(", ", _graph.ArtifactProducers.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
    }

    public IReadOnlyList<OutputVariable> CommittedVariables => _committedVariables;

    public IReadOnlyList<ArtifactRef> WrittenArtifacts => _writtenArtifacts;

    public string Summary { get; private set; } = "";

    public void SetSummary(string summary) => Summary = summary;

    // ---- reading artifacts ---------------------------------------------------------------

    /// <summary>The body of a declared read artifact, scoped to this step's iteration target.</summary>
    public string ArtifactBody(string artifactId)
    {
        RequireDeclaredArtifactRead(artifactId);
        var producer = _graph.ById(_graph.ArtifactProducers[artifactId]);
        var scopedTarget = producer.IteratesOver is null ? null : IterationTarget;
        return Artifacts.Read(_index.Require(artifactId, scopedTarget).Path).Body;
    }

    /// <summary>Every in-force version of a declared read artifact produced by an iterating step, in list order.</summary>
    public IReadOnlyList<(string Target, string Body)> AllArtifactBodies(string artifactId)
    {
        RequireDeclaredArtifactRead(artifactId);
        var producer = _graph.ById(_graph.ArtifactProducers[artifactId]);
        if (producer.IteratesOver is null)
        {
            throw new HaltException(
                $"Step '{Step.Id}' asked for every version of artifact '{artifactId}', but '{producer.Id}' does not " +
                "iterate and produces exactly one.");
        }

        var order = _reconciler.IterationOrderOf(producer.IteratesOver);
        return [.. order.Select(t => (t, Artifacts.Read(_index.Require(artifactId, t).Path).Body))];
    }

    public string ArtifactPath(string artifactId)
    {
        RequireDeclaredArtifactRead(artifactId);
        var producer = _graph.ById(_graph.ArtifactProducers[artifactId]);
        var scopedTarget = producer.IteratesOver is null ? null : IterationTarget;
        return _index.Require(artifactId, scopedTarget).Path;
    }

    /// <summary>
    /// The frozen iteration order behind a collection read - the order the variable's *producer*
    /// iterated over, which is generally not this step's own. A report step that does not iterate
    /// at all still reads every value an upstream iterating step committed, and it must get them
    /// in the frozen list's order so they line up with AllArtifactBodies.
    /// </summary>
    private IReadOnlyList<string> IterationOrderProducing(string variableName)
    {
        var producer = _graph.ById(_graph.VariableProducers[variableName]);
        if (producer.IteratesOver is not { } frozenList)
        {
            throw new HaltException(
                $"Step '{Step.Id}' read '{variableName}[]' as a collection, but '{producer.Id}' does not iterate " +
                "and commits a single value. The graph validator rejects this at startup; reaching it here means " +
                "the declaration and the code disagree.");
        }

        return _reconciler.IterationOrderOf(frozenList);
    }

    private void RequireDeclaredArtifactRead(string artifactId)
    {
        if (!Step.ReadsArtifacts.Contains(artifactId))
        {
            throw new HaltException(
                $"Step '{Step.Id}' read artifact '{artifactId}', which it did not declare. " +
                $"Declared read artifacts are: {(Step.ReadsArtifacts.Count == 0 ? "(none)" : string.Join(", ", Step.ReadsArtifacts))}.");
        }
    }

    // ---- committing ------------------------------------------------------------------------

    public void CommitVariable(string name, string value)
    {
        if (!Step.WritesVariables.Contains(name))
        {
            throw new HaltException(
                $"Step '{Step.Id}' committed variable '{name}', which it did not declare as an output. " +
                $"Declared outputs are: {(Step.WritesVariables.Count == 0 ? "(none)" : string.Join(", ", Step.WritesVariables))}.");
        }

        var scoped = ProjectState.Namespaced(name, Step.IteratesOver is null ? null : IterationTarget);
        _state.Commit(scoped, value, Step.Id, reExecuting: true);
        _committedVariables.Add(OutputVariable.Of(scoped, value));
    }

    /// <summary>
    /// Writes a new artifact version with a complete origin header (feature 2.1) and adds it to
    /// the in-force index. The artifact lands before the completion record that names it - the
    /// ordering the whole persistence design exists to provide.
    /// </summary>
    public ArtifactRef WriteArtifact(string artifactId, string body, ModelCall? call = null, Action<MdDoc>? extraHeader = null)
    {
        if (!Step.WritesArtifacts.Contains(artifactId))
        {
            throw new HaltException(
                $"Step '{Step.Id}' wrote artifact '{artifactId}', which it did not declare as an output. " +
                $"Declared outputs are: {(Step.WritesArtifacts.Count == 0 ? "(none)" : string.Join(", ", Step.WritesArtifacts))}.");
        }

        var existing = Artifacts.ExistingVersions(artifactId, IterationTarget);
        var version = existing.Count == 0 ? 1 : existing[^1] + 1;

        var origin = new ArtifactOrigin
        {
            ArtifactId = artifactId,
            Version = version,
            ProducingStepId = Step.Id,
            IterationTarget = IterationTarget,
            RunId = Records.Run.Id,
            Sequence = Records.Run.NextSequence(),
            TimestampUtc = RunIdentity.TimestampUtc(),
            Inputs = Inputs,
            SupersedesVersion = existing.Count == 0 ? "-" : existing[^1].ToString(),
            SupersededBecause = existing.Count == 0 ? "-" : SupersedeReason
        };

        if (call is not null)
        {
            origin = origin with
            {
                PromptTemplatePath = call.Prompt.TemplatePath,
                PromptTemplateHash = call.Prompt.TemplateHash,
                ResolvedPromptHash = call.Prompt.TextHash,
                ModelRequested = call.Prompt.Config.Model,
                ModelReported = call.Result.ModelReported,
                Temperature = PromptTemplate.Format(call.Prompt.Config.Temperature),
                TopP = PromptTemplate.Format(call.Prompt.Config.TopP),
                MaxTokens = call.Prompt.Config.MaxTokens.ToString(),
                Seed = call.Prompt.Config.Seed.ToString(),
                ResponseId = call.Result.ResponseId,
                SystemFingerprint = call.Result.SystemFingerprint,
                UsagePromptTokens = call.Result.PromptTokens,
                UsageCompletionTokens = call.Result.CompletionTokens,
                UsageTotalTokens = call.Result.TotalTokens,
                RawResponseRecord = call.Result.ResponseRecordPath
            };
        }

        var reference = Artifacts.Write(origin, body, extraHeader);
        _index.Put(reference);
        _writtenArtifacts.Add(reference);

        Emit.To(
            Surface.Console,
            EventKinds.ArtifactWritten,
            $"Wrote {reference.Path}",
            Emit.Fields(
                "step", Step.Id,
                "artifact", artifactId,
                "version", version.ToString(),
                "hash", reference.Hash,
                "supersedes", origin.SupersedesVersion));

        return reference;
    }

    // ---- the model -------------------------------------------------------------------------

    /// <summary>
    /// Loads this step's declared template, substitutes the supplied values strictly in both
    /// directions, and calls the model. Every parameter of the call comes from the template's
    /// front matter; this method contributes nothing.
    /// </summary>
    public ModelCall CallModel(params (string Name, string Value, string Source)[] values)
    {
        var templateRelative = Step.PromptTemplate
            ?? throw new HaltException($"Step '{Step.Id}' called the model without declaring a prompt template.");

        var absolute = Path.Combine(PromptsDirectory, templateRelative);
        var displayPath = $"prompts/{templateRelative}";
        var (config, body, fileHash) = PromptTemplate.Load(absolute, displayPath);

        // Feature 4.4 - substituted values are recorded with their source: which variable, what
        // value, and which step produced it.
        var supplied = values
            .Select(v => new SubstitutedValue(v.Name, v.Value, Canonical.HashValue(v.Value), v.Source))
            .ToList();

        var prompt = PromptTemplate.Resolve(displayPath, fileHash, config, body, supplied);
        var result = Llm.Call(Step.Id, IterationTarget, prompt, Control.Shutdown.Token);
        return new ModelCall(result, prompt);
    }

    /// <summary>
    /// Feature 4.5 / 4.6 - parsing is a separate act from the call, selected by the template's
    /// declaration, and it is never destructive of the response record. The declared output set
    /// is checked against what the parser produced, in both directions.
    /// </summary>
    public IReadOnlyDictionary<string, string> ParseResponse(ModelCall call)
    {
        var parser = OutputParsers.Require(call.Prompt.Config.Parser, call.Prompt.TemplatePath);
        var parsed = parser.Parse(call.Result.Content, call.Result.ResponseRecordPath);
        OutputParsers.RequireDeclaredOutputs(
            call.Prompt.Config.OutputVariables, parsed, call.Prompt.TemplatePath, parser.Id);
        return parsed;
    }

    // ---- decisions -------------------------------------------------------------------------

    /// <summary>
    /// Feature 6.1 / 6.2 - blocks for a human decision over a code-defined closed enum. The
    /// question is a file; the answer may be written by the browser or by the operator's editor,
    /// and this method cares only that a valid record exists.
    ///
    /// It does not return until an answer exists, and it never selects a default.
    /// </summary>
    public string AwaitDecision(
        string question,
        IReadOnlyList<string> permittedAnswers,
        string codeLocation,
        IReadOnlyList<string> relatedArtifacts)
    {
        if (permittedAnswers.Count < 2)
        {
            throw new HaltException(
                $"Step '{Step.Id}' raised a decision with {permittedAnswers.Count} permitted answer(s). " +
                "A decision with one option is not a decision.");
        }

        // Each invalidation of this step retires exactly one round of question-and-answer, so the
        // open round is simply one past however many have been retired (feature 6.3).
        var version = Records.ReadInvalidations(Step.Id, IterationTarget).Count + 1;

        if (!Step.RaisesDecision)
        {
            throw new HaltException(
                $"Step '{Step.Id}' raised a decision without declaring RaisesDecision. The declaration is what puts " +
                "the answer record into the step's inputs, and without it an edited answer would go unnoticed " +
                "(feature 6.3).");
        }

        var existing = Records.ReadAnswer(Step.Id, IterationTarget, version);
        if (existing is not null)
        {
            var value = ValidateAnswer(existing, permittedAnswers);
            RecordAnswerAsInput(version);
            return value;
        }

        var questionRecord = Records.ReadQuestion(Step.Id, IterationTarget, version)
            ?? Records.WriteQuestion(
                Step.Id, IterationTarget, version, question, permittedAnswers,
                codeLocation, Inputs, relatedArtifacts);

        var block = new PendingBlock(
            Step.Id,
            IterationTarget,
            version,
            question,
            permittedAnswers,
            questionRecord.Path,
            Paths.Relative(Path.Combine(Paths.RecordDirectory(Step.Id, IterationTarget), RecordStore.AnswerFileName(version))));

        Control.Block = block;
        Control.Phase = RunPhase.Blocked;

        // Feature 6.5 - a block arising mid-run is reported by the browser console alone. The
        // terminal does not echo it: that would give workflow state a second owning surface,
        // which Pillar 2 rules out by definition. The run log on disk is the recovery path for a
        // block that occurs while nobody has the browser open.
        Emit.To(
            Surface.Console,
            EventKinds.Block,
            question,
            Emit.Fields(
                "step", Step.Id,
                "iteration_target", IterationTarget ?? "-",
                "permitted_answers", string.Join(" | ", permittedAnswers),
                "question_record", questionRecord.Path,
                "answer_record", block.AnswerPath,
                "raised_by", codeLocation),
            data: block);

        while (true)
        {
            if (Control.StopRequested)
            {
                throw new StopRequestedException();
            }

            Control.Shutdown.Token.ThrowIfCancellationRequested();

            var answer = Records.ReadAnswer(Step.Id, IterationTarget, version);
            if (answer is not null)
            {
                var value = ValidateAnswer(answer, permittedAnswers);
                RecordAnswerAsInput(version);
                Control.Block = null;
                Control.Phase = RunPhase.Running;

                Emit.To(
                    Surface.Console,
                    EventKinds.BlockResolved,
                    $"Answered '{value}' via {answer.Surface}.",
                    Emit.Fields(
                        "step", Step.Id,
                        "answer", value,
                        "answer_surface", answer.Surface,
                        "answered_utc", answer.TimestampUtc,
                        "answer_record", answer.Path));

                return value;
            }

            Control.Shutdown.Token.WaitHandle.WaitOne(250);
        }
    }

    /// <summary>
    /// Adds the answer in force to this step's inputs, so the completion record names it by hash.
    /// The next run recomputes the identical entry from the step's RaisesDecision declaration; if
    /// the two ever disagree, the reconciler halts rather than skipping (feature 6.3).
    /// </summary>
    private void RecordAnswerAsInput(int version)
    {
        var path = Path.Combine(Paths.RecordDirectory(Step.Id, IterationTarget), RecordStore.AnswerFileName(version));
        var input = StepInputs.AnswerInput(Paths, path);
        if (!_inputs.Any(i => i.Kind == input.Kind && i.Name == input.Name))
        {
            _inputs.Add(input);
        }
    }

    private string ValidateAnswer(AnswerRecord answer, IReadOnlyList<string> permitted)
    {
        // Feature 6.1 - an answer value outside the closed enum is a halt: malformed input, not a
        // re-ask. Re-asking would let a badly formed answer quietly become a retry loop, and the
        // operator would never learn their editor wrote something the code cannot accept.
        if (!permitted.Contains(answer.Answer))
        {
            throw new HaltException(
                $"The answer record {answer.Path} says '{answer.Answer}', which is outside the closed set of " +
                $"permitted answers ({string.Join(", ", permitted)}) defined by '{Step.Id}'. " +
                "This is malformed input, not a question to ask again.");
        }

        return answer.Answer;
    }

    /// <summary>
    /// Feature 2.8 - records a branch: the predicate's identity, the exact input value it
    /// evaluated, the enumerated options, the chosen branch, and the step that follows.
    /// </summary>
    public void RecordBranch(
        string predicateIdentity,
        string evaluatedInput,
        string evaluatedValue,
        IReadOnlyList<string> options,
        string chosenBranch,
        string nextStep,
        string explanation)
    {
        var path = Records.WriteDecision(
            Step.Id, IterationTarget, predicateIdentity, evaluatedInput,
            evaluatedValue, options, chosenBranch, nextStep, explanation);

        Emit.To(
            Surface.Console,
            EventKinds.Decision,
            $"{predicateIdentity}: {evaluatedInput} = '{evaluatedValue}' → {chosenBranch}",
            Emit.Fields(
                "step", Step.Id,
                "predicate", predicateIdentity,
                "evaluated_input", evaluatedInput,
                "evaluated_value", evaluatedValue,
                "options", string.Join(" | ", options),
                "chosen_branch", chosenBranch,
                "next_step", nextStep,
                "decision_record", path));
    }

    public void Note(string kind, string message, IReadOnlyList<KeyValuePair<string, string>>? fields = null)
        => Emit.To(Surface.Console, kind, message, fields);
}
