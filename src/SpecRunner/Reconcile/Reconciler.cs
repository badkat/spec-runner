using System.Text;
using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Graph;
using SpecRunner.Records;
using SpecRunner.State;
using SpecRunner.Workflow;

namespace SpecRunner.Reconcile;

/// <summary>
/// The engine. This is not a repair path: it runs in full on every startup, before any step
/// executes, and reconciling records against the code's dependency graph is the normal path on
/// every run.
///
/// Replay is not blind skipping. Before a completion record is honored, the inputs it names must
/// still match what is on disk. Where they do not - including because the developer edited a file
/// by hand - the record is stale, and it is invalidated along with everything derived from it.
/// </summary>
public sealed class Reconciler(
    DependencyGraph graph,
    RecordStore records,
    ArtifactStore artifacts,
    ProjectPaths paths,
    string promptsDirectory)
{
    private readonly Dictionary<string, FrozenIterationList> _frozenLists = new(StringComparer.Ordinal);
    private ArtifactIndex _index = new();

    public ReconcileResult Run()
    {
        // Feature 1.12 - detection happens in the same startup pass as orphan detection and state
        // reconciliation, and reports every unrecognized file at once rather than just the first.
        var unrecognized = ProjectScan.FindUnrecognizedFiles(paths);
        if (unrecognized.Count > 0)
        {
            throw new HaltException(BuildUnrecognizedFileReport(unrecognized));
        }

        var state = new ProjectState();
        var index = _index = new ArtifactIndex();
        var invalidations = new List<InvalidationRecord>();
        var handEdited = new List<string>();
        var plan = new List<PlannedStep>();
        var invalidatedClosure = new HashSet<string>(StringComparer.Ordinal);

        var definiteModelCalls = 0;
        var modelCallCountIsLowerBound = false;

        foreach (var step in graph.Steps)
        {
            var mustExecute = invalidatedClosure.Contains(step.Id);

            // An iterating step needs its frozen list to enumerate per-item rows. When the list's
            // producer is itself re-executing, the list is not yet on disk and the honest answer
            // is that the item count is not knowable until it runs (feature 5.1).
            IReadOnlyList<string?>? targets = null;
            if (step.IteratesOver is { } listArtifactId)
            {
                if (mustExecute || !index.Has(listArtifactId, null))
                {
                    CascadeInvalidateRecordedTargets(step, invalidations, invalidatedClosure);
                    plan.Add(new PlannedStep(
                        step.Id, null, step.Description, StepAction.Execute,
                        $"iterates over '{listArtifactId}', which is being re-produced this run; item count resolves once it exists",
                        step.PromptTemplate is not null, "-", [])
                    {
                        ItemsPending = true
                    });

                    if (step.PromptTemplate is not null)
                    {
                        modelCallCountIsLowerBound = true;
                    }

                    MarkDownstream(step.Id, invalidatedClosure);
                    continue;
                }

                targets = [.. FrozenListFor(listArtifactId).Identities];
            }

            foreach (var target in targets ?? [null])
            {
                // A guard reads only declared variables, so when the step is not already condemned
                // by the cascade its guard is evaluable: everything it reads was committed by an
                // upstream step that skipped, or was never produced because that upstream step did
                // not apply either - which is itself a decided answer, not an unknown one.
                if (!mustExecute && step.Guard is { } guard)
                {
                    var verdict = EvaluateGuard(guard, step, target, state);
                    if (verdict.Reason is { } reason)
                    {
                        plan.Add(new PlannedStep(
                            step.Id, target, step.Description, StepAction.NotApplicable, reason, false, "-", []));
                        continue;
                    }
                }

                var planned = ReconcileOne(step, target, mustExecute, state, index, invalidations, handEdited);
                plan.Add(planned);

                if (planned.Action == StepAction.Execute)
                {
                    MarkDownstream(step.Id, invalidatedClosure);
                    if (planned.CallsModel)
                    {
                        definiteModelCalls++;
                    }
                }
            }
        }

        var orphans = FindOrphans(index);
        var divergences = StateProjection.Diff(paths, state, index);

        return new ReconcileResult(
            plan,
            definiteModelCalls,
            modelCallCountIsLowerBound,
            state,
            index,
            invalidations,
            orphans,
            handEdited,
            records.IncompleteModelCalls(),
            FindStartupBlocks(plan),
            divergences);
    }

    /// <summary>Decides skip or execute for one step, or one item of one step, and records why.</summary>
    private PlannedStep ReconcileOne(
        Step step,
        string? target,
        bool mustExecute,
        ProjectState state,
        ArtifactIndex index,
        List<InvalidationRecord> invalidations,
        List<string> handEdited)
    {
        var callsModel = step.PromptTemplate is not null;
        var record = records.InForceCompletion(step.Id, target);

        if (record is null)
        {
            // Feature 1.9 - a missing record means not-done, and triggers re-execution plus a
            // downstream cascade. Deleting a record is the sanctioned "redo this step" gesture.
            return new PlannedStep(
                step.Id, target, step.Description, StepAction.Execute,
                "no completion record in force", callsModel, "-", []);
        }

        if (mustExecute)
        {
            var invalidation = records.WriteInvalidation(
                step.Id, target, record.Path, InvalidationCause.UpstreamCascade,
                "(whole step)", record.Path, "-", "reconciler",
                "An upstream step in the declared dependency graph is being re-executed this run, so everything " +
                "reachable downstream of it is presumed invalid until re-established (Pillar 6, feature 1.6).");
            invalidations.Add(invalidation);

            return new PlannedStep(
                step.Id, target, step.Description, StepAction.Execute,
                "invalidated — upstream cascade", callsModel, record.Path, record.Inputs);
        }

        // Feature 1.5 - changing the canonicalization rules invalidates everything at once,
        // loudly and on purpose, because every hash in the record was taken under rules that no
        // longer exist.
        if (record.CanonicalizationVersion != Canonical.Version)
        {
            var invalidation = records.WriteInvalidation(
                step.Id, target, record.Path, InvalidationCause.CanonicalizationVersionBump,
                "(all hashes)", $"canonicalization v{record.CanonicalizationVersion}",
                $"canonicalization v{Canonical.Version}", "reconciler",
                "This record's hashes were taken under canonicalization rules this build no longer uses.");
            invalidations.Add(invalidation);

            return new PlannedStep(
                step.Id, target, step.Description, StepAction.Execute,
                $"invalidated — canonicalization version bump (v{record.CanonicalizationVersion} → v{Canonical.Version})",
                callsModel, record.Path, record.Inputs);
        }

        var current = StepInputs.Compute(
            step, target, graph, state, index, paths, promptsDirectory, records, FrozenOrder);

        var difference = StepInputs.FirstDifference(record.Inputs, current);
        if (difference is not null)
        {
            // Feature 6.3 - a decision, once answered, is in force and cannot be changed by simply
            // writing a different answer to the same question. Two disagreeing authoritative
            // answers is a conflict with an immutable decision, not something to silently resolve
            // by picking one - and not something to quietly turn into a fresh question either,
            // which would let a single hand-edit retire a decision the operator never invalidated.
            if (StepInputs.IsAnswerRecord(difference))
            {
                throw new HaltException(
                    $"The answer record {difference.Name} has changed since '{step.Id}' consumed it " +
                    $"(recorded {difference.ExpectedHash}, now {difference.ActualHash}).\n\n" +
                    "A decision, once answered, is in force. Revision is a deliberate two-step act:\n" +
                    "  1. Invalidate the existing decision — from the console, or by deleting this step's\n" +
                    $"     completion record ({record.Path}) by hand.\n" +
                    "  2. Only once the old decision is no longer in force does the question open again,\n" +
                    "     as a new numbered round, and a new answer file is accepted.\n\n" +
                    "The superseded records stay on disk either way; revision never edits history, it\n" +
                    "supersedes it (feature 6.3).")
                {
                    StepId = step.Id,
                    IterationTarget = target
                };
            }

            // Feature 1.8 - an artifact whose body no longer matches the hash recorded in its own
            // origin header was edited by a person; an artifact whose *inputs* changed is stale.
            // Both accept the on-disk content as truth; they are reported differently because
            // they mean different things to the operator.
            var cause = InvalidationCause.InputHashMismatch;
            var explanation =
                $"Input '{difference.Name}' changed since this step ran. The record named {difference.ExpectedHash}; " +
                $"the content on disk now hashes to {difference.ActualHash}. The on-disk content is taken as truth " +
                "and everything derived from this step is invalidated with it.";

            if (difference.Kind == InputRef.FileKind && IsHandEditedArtifact(difference.Name))
            {
                cause = InvalidationCause.ArtifactHandEdited;
                explanation =
                    $"Artifact '{difference.Name}' no longer matches the body hash recorded in its own origin header: " +
                    "a person edited it. That edit is accepted as truth (Pillar 7), and this step - which consumed " +
                    "the earlier content - is invalidated along with everything derived from it.";

                if (!handEdited.Contains(difference.Name))
                {
                    handEdited.Add(difference.Name);
                }
            }

            var invalidation = records.WriteInvalidation(
                step.Id, target, record.Path, cause, difference.Name,
                difference.ExpectedHash, difference.ActualHash, "reconciler", explanation);
            invalidations.Add(invalidation);

            var label = cause == InvalidationCause.ArtifactHandEdited
                ? $"invalidated — hand-edited input '{difference.Name}'"
                : $"invalidated — stale input '{difference.Name}'";

            return new PlannedStep(step.Id, target, step.Description, StepAction.Execute, label, callsModel, record.Path, current);
        }

        // The record stands. Apply what it committed, so downstream steps can be reconciled
        // against the same state the run would produce.
        foreach (var variable in record.OutputVariables)
        {
            state.Commit(variable.Name, variable.Value, step.Id, reExecuting: true);
        }

        foreach (var artifact in record.OutputArtifacts)
        {
            index.Put(artifact);
        }

        return new PlannedStep(
            step.Id, target, step.Description, StepAction.Skip,
            "record in force; all inputs match", callsModel, record.Path, record.Inputs);
    }

    private void CascadeInvalidateRecordedTargets(
        Step step,
        List<InvalidationRecord> invalidations,
        HashSet<string> closure)
    {
        foreach (var target in records.RecordedTargets(step.Id))
        {
            var record = records.InForceCompletion(step.Id, target);
            if (record is null)
            {
                continue;
            }

            invalidations.Add(records.WriteInvalidation(
                step.Id, target, record.Path, InvalidationCause.UpstreamCascade,
                "(iteration source)", record.Path, "-", "reconciler",
                $"The frozen iteration list '{step.IteratesOver}' is being re-produced this run, so every item " +
                "of this step is presumed invalid until re-established (feature 1.6)."));
        }

        MarkDownstream(step.Id, closure);
    }

    private void MarkDownstream(string stepId, HashSet<string> closure)
    {
        // Feature 1.6 - transitive invalidation is computed from the graph, in the same pass, and
        // is never local to one step. It is computed rather than stored, on purpose.
        foreach (var downstream in graph.DownstreamClosure(stepId))
        {
            closure.Add(downstream);
        }
    }

    private bool IsHandEditedArtifact(string relativePath)
    {
        if (!relativePath.StartsWith("artifacts/", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return artifacts.Read(relativePath).WasHandEdited;
        }
        catch (HaltException)
        {
            return false;
        }
    }

    /// <summary>
    /// The guard's answer, or - when a variable it reads was never produced because the step that
    /// produces it did not itself apply - the reason this step does not apply either. That is a
    /// decided answer arrived at by code, not a default chosen to keep moving (Pillar 8).
    /// </summary>
    private static (bool Value, string? Reason) EvaluateGuard(StepGuard guard, Step step, string? target, ProjectState state)
    {
        foreach (var name in guard.ReadsVariables)
        {
            var producerScoped = ProjectState.Namespaced(name, target);
            if (!state.Has(name) && !state.Has(producerScoped))
            {
                return (false,
                    $"guard '{guard.Description}' reads '{name}', which was never committed because the step that " +
                    "produces it did not apply");
            }
        }

        var scoped = new ScopedState(state, step.Id, step.ReadsVariables, target, iterationOrderOf: null);
        return guard.Predicate(scoped)
            ? (true, null)
            : (false, $"guard '{guard.Description}' evaluated false");
    }

    private FrozenIterationList FrozenListFor(string artifactId)
    {
        if (_frozenLists.TryGetValue(artifactId, out var cached))
        {
            return cached;
        }

        var reference = _index.Require(artifactId, null);
        var list = FrozenIterationList.Read(artifacts.Read(reference.Path));
        _frozenLists[artifactId] = list;
        return list;
    }

    private IReadOnlyList<string> FrozenOrder(string artifactId) => FrozenListFor(artifactId).Identities;

    /// <summary>Loads a frozen list mid-run, once the step that produces it has committed.</summary>
    public FrozenIterationList LoadFrozenList(string artifactId, ArtifactIndex index)
    {
        _index = index;
        _frozenLists.Remove(artifactId);
        return FrozenListFor(artifactId);
    }

    /// <summary>The same input computation the pre-flight used, for the runner to record at commit time.</summary>
    public IReadOnlyList<InputRef> ComputeInputs(Step step, string? target, ProjectState state, ArtifactIndex index)
    {
        _index = index;
        return StepInputs.Compute(step, target, graph, state, index, paths, promptsDirectory, records, FrozenOrder);
    }

    public IReadOnlyList<string> IterationOrderOf(string frozenListArtifactId) => FrozenOrder(frozenListArtifactId);

    /// <summary>
    /// Feature 1.11 - artifacts on disk that no honored record names are enumerated at startup
    /// with the run that likely produced them. They are never loaded as input.
    /// </summary>
    private IReadOnlyList<OrphanedArtifact> FindOrphans(ArtifactIndex index)
    {
        if (!Directory.Exists(paths.Artifacts))
        {
            return [];
        }

        var named = index.All().Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        var orphans = new List<OrphanedArtifact>();

        foreach (var file in Directory.GetFiles(paths.Artifacts, "*.md", SearchOption.AllDirectories))
        {
            var relative = paths.Relative(file);
            if (named.Contains(relative))
            {
                continue;
            }

            try
            {
                var doc = MdDoc.Parse(File.ReadAllText(file), relative);
                orphans.Add(new OrphanedArtifact(
                    relative,
                    doc.Optional("run_id") ?? "(unrecorded)",
                    doc.Optional("producing_step_id") ?? "(unrecorded)"));
            }
            catch (HaltException)
            {
                // A .md file under artifacts/ that this application did not write. It is an
                // orphan like any other - enumerated, reported, and never loaded as input - and
                // reporting it that way is more useful than crashing on its malformed header,
                // because the operator's next action is the same either way.
                orphans.Add(new OrphanedArtifact(relative, "(no readable origin header)", "(not written by this workflow)"));
            }
        }

        return [.. orphans.OrderBy(o => o.RelativePath, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Feature 6.5 - an unanswered question that already exists when the run begins is a startup
    /// block, reported by the terminal in the same bucket as other startup self-checks.
    /// </summary>
    private IReadOnlyList<StartupBlock> FindStartupBlocks(IReadOnlyList<PlannedStep> plan)
    {
        var blocks = new List<StartupBlock>();

        foreach (var planned in plan)
        {
            if (planned.Action != StepAction.Execute || planned.ItemsPending)
            {
                continue;
            }

            var version = records.CurrentQuestionVersion(planned.StepId, planned.IterationTarget);
            var question = records.ReadQuestion(planned.StepId, planned.IterationTarget, version);
            if (question is null || records.ReadAnswer(planned.StepId, planned.IterationTarget, version) is not null)
            {
                continue;
            }

            blocks.Add(new StartupBlock(planned.StepId, planned.IterationTarget, question.Path, question.Question));
        }

        return blocks;
    }

    private static string BuildUnrecognizedFileReport(IReadOnlyList<UnrecognizedFile> unrecognized)
    {
        var report = new StringBuilder();
        report.Append(unrecognized.Count)
              .Append(unrecognized.Count == 1 ? " unrecognized file was" : " unrecognized files were")
              .Append(" found in the project tree. The run does not begin until they are cleared.\n");

        foreach (var file in unrecognized)
        {
            report.Append("\n  ").Append(file.RelativePath).Append("\n      ").Append(file.Reason);
        }

        report.Append(
            "\n\nEverything in the project directory must be a .md file the workflow itself produced, except:" +
            $"\n  - these directories, which the workflow does not own: {string.Join(", ", ProjectPaths.AllowedForeignDirectories)}" +
            $"\n  - the operator-note convention: anything under '{ProjectPaths.NotesDirectoryName}/', and any file named '*{ProjectPaths.NoteFileSuffix}'" +
            "\n\nResolution is manual and out-of-band: delete, move, or fix the files above outside the" +
            "\napplication and run again. There is no acknowledge-and-keep path, and the allowlist is" +
            "\nstatic and checked in - changing it is a code change like any other (feature 1.12).");

        return report.ToString();
    }

    /// <summary>
    /// Feature 1.10 - the plan rendered before execution, and written to disk as part of the run
    /// log. Feature 8.6's per-step detail is carried in the same payload so the browser can show
    /// declared inputs with hashes, the resolved target, and the skip/execute reason for every
    /// row, including the skipped ones.
    /// </summary>
    public static string RenderPlan(ReconcileResult result)
    {
        var text = new StringBuilder();
        text.Append("Pre-flight plan — ").Append(result.Plan.Count).Append(" step rows\n\n");

        foreach (var planned in result.Plan)
        {
            var mark = planned.Action switch
            {
                StepAction.Skip => "skip           ",
                StepAction.Execute => "execute        ",
                StepAction.NotApplicable => "not-applicable ",
                _ => "?              "
            };

            text.Append("  ").Append(mark).Append(planned.StepId);
            if (planned.IterationTarget is { } target)
            {
                text.Append(" [").Append(target).Append(']');
            }

            text.Append("\n                 ").Append(planned.Reason);
            if (planned.CallsModel)
            {
                text.Append(" · calls the model");
            }

            text.Append('\n');
        }

        text.Append("\n  Model calls this run: ")
            .Append(result.DefiniteModelCalls)
            .Append(result.ModelCallCountIsLowerBound ? " so far, plus one per item of every iteration not yet frozen" : "")
            .Append('\n');

        return text.ToString();
    }
}
