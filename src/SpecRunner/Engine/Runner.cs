using System.Diagnostics;
using SpecRunner.Core;
using SpecRunner.Surfaces;
using SpecRunner.Graph;
using SpecRunner.Llm;
using SpecRunner.Reconcile;
using SpecRunner.Records;
using SpecRunner.State;
using SpecRunner.Workflow;

namespace SpecRunner.Engine;

/// <summary>
/// Executes the workflow as a single ordered sequence with exactly one unit in flight.
///
/// Pillar 5's test is whether an observer can, at any instant, name the single thing the
/// application is doing. There is one thread here, one loop, and no place for a second unit of
/// work to start: no parallel iteration, no prefetch, no speculative execution. The operator's
/// controls - stop, state rebuild, forced invalidation - are flags read at step boundaries, so
/// even they never overlap with a step.
/// </summary>
public sealed class Runner(
    DependencyGraph graph,
    Reconciler reconciler,
    RecordStore records,
    ArtifactStore artifacts,
    LlmClient llm,
    RunControl control,
    ProjectPaths paths,
    string promptsDirectory)
{
    /// <summary>
    /// Feature 7.4 - if the same upstream target is invalidated by the same downstream validator
    /// more than this many times, the run halts and presents the full revision history rather
    /// than looping. A constant in code, not a runtime or file-driven setting: an
    /// upstream/downstream pair that cannot converge is a genuine judgment the application cannot
    /// make, and introducing a knob for it would only let the operator postpone making it.
    /// </summary>
    public const int RevisionPingPongThreshold = 3;

    public void Execute(ReconcileResult reconciled)
    {
        var state = reconciled.State;
        var index = reconciled.Artifacts;
        var planByKey = reconciled.Plan.ToDictionary(p => Key(p.StepId, p.IterationTarget), StringComparer.Ordinal);

        control.Phase = RunPhase.Running;
        Emit.To(
            Surface.Console,
            EventKinds.RunStarted,
            $"Run {records.Run.Id} started by the operator.",
            Emit.Fields("run_id", records.Run.Id, "steps", graph.Steps.Count.ToString()));

        try
        {
            foreach (var step in graph.Steps)
            {
                HandleBoundaryRequests(state, index);

                if (step.IteratesOver is { } listArtifactId)
                {
                    RunIterating(step, listArtifactId, state, index, planByKey);
                }
                else
                {
                    RunOne(step, null, [], state, index, planByKey);
                }
            }

            control.Phase = RunPhase.Completed;
            var outcome = records.WriteRunOutcome("completed", "Every step in the workflow is complete.", null, null);
            Emit.To(
                Surface.Console,
                EventKinds.RunCompleted,
                "Workflow complete. Every step has an in-force completion record.",
                Emit.Fields("run_id", records.Run.Id, "outcome_record", outcome));
        }
        catch (StopRequestedException)
        {
            control.Phase = RunPhase.Stopped;
            var current = control.Current;
            var outcome = records.WriteRunOutcome(
                "stopped-by-operator",
                $"Stopped by the operator at {current?.StepId ?? "(between steps)"}. " +
                "The in-flight step ran to its commit before the flag was read; nothing is half-applied.",
                current?.StepId, current?.IterationTarget);

            Emit.To(
                Surface.Console,
                EventKinds.RunStopped,
                $"Stopped by operator at {current?.StepId ?? "a step boundary"}. Run again to resume.",
                Emit.Fields("run_id", records.Run.Id, "outcome_record", outcome));
        }
    }

    // ---- the sequence ----------------------------------------------------------------------

    private void RunIterating(
        Step step,
        string listArtifactId,
        ProjectState state,
        ArtifactIndex index,
        IReadOnlyDictionary<string, PlannedStep> plan)
    {
        var list = reconciler.LoadFrozenList(listArtifactId, index);

        Emit.To(
            Surface.Console,
            EventKinds.IterationFrozen,
            $"Iterating '{step.Id}' over {list.Items.Count} frozen item(s) from {list.ArtifactPath}.",
            Emit.Fields(
                "step", step.Id,
                "frozen_list", list.ArtifactPath,
                "iteration_source", list.SourceArtifactPath,
                "iteration_source_hash", list.SourceHash,
                "items", list.Items.Count.ToString()));

        var order = list.Identities;

        foreach (var item in list.Items)
        {
            HandleBoundaryRequests(state, index);

            // Feature 5.2 - drift in the underlying set is detected at item boundaries and halts.
            // Finishing against a list nobody can reconstruct is worse than stopping.
            AssertNoIterationDrift(list, step);

            Emit.To(
                Surface.Console,
                EventKinds.IterationItem,
                $"Item {item.Ordinal} of {list.Items.Count}: {item.Identity}",
                Emit.Fields(
                    "step", step.Id,
                    "ordinal", item.Ordinal.ToString(),
                    "identity", item.Identity,
                    "text", item.Text));

            RunOne(step, item.Identity, order, state, index, plan);
        }
    }

    private void RunOne(
        Step step,
        string? target,
        IReadOnlyList<string> iterationOrder,
        ProjectState state,
        ArtifactIndex index,
        IReadOnlyDictionary<string, PlannedStep> plan)
    {
        if (step.Guard is { } guard)
        {
            var scoped = new ScopedState(state, step.Id, step.ReadsVariables, target, iterationOrderOf: null);
            if (!GuardInputsAvailable(guard, state, target) || !guard.Predicate(scoped))
            {
                Emit.To(
                    Surface.Console,
                    EventKinds.StepNotApplicable,
                    $"{step.Id} does not apply: guard '{guard.Description}' is false.",
                    Emit.Fields("step", step.Id, "iteration_target", target ?? "-", "guard", guard.Description));
                return;
            }
        }

        var record = records.InForceCompletion(step.Id, target);
        if (record is not null)
        {
            var current = reconciler.ComputeInputs(step, target, state, index);
            var difference = StepInputs.FirstDifference(record.Inputs, current);
            if (difference is not null)
            {
                throw new HaltException(
                    $"'{step.Id}' has an in-force completion record whose input '{difference.Name}' no longer matches " +
                    $"({difference.ExpectedHash} vs {difference.ActualHash}), but reconciliation honored it. " +
                    "Reconciliation and execution disagree, which means one of them is wrong; the application will " +
                    "not proceed on either.")
                {
                    StepId = step.Id,
                    IterationTarget = target
                };
            }

            foreach (var variable in record.OutputVariables)
            {
                state.Commit(variable.Name, variable.Value, step.Id, reExecuting: true);
            }

            foreach (var artifact in record.OutputArtifacts)
            {
                index.Put(artifact);
            }

            // Feature 8.6 - skipped steps are shown too, with the record that justified skipping.
            // An invisible skip is a hidden state transition.
            var plannedReason = plan.TryGetValue(Key(step.Id, target), out var planned) ? planned.Reason : "record in force";
            Emit.To(
                Surface.Console,
                EventKinds.StepSkipped,
                $"{step.Id}{(target is null ? "" : $" [{target}]")} — skipped; {plannedReason}",
                Emit.Fields(
                    "step", step.Id,
                    "iteration_target", target ?? "-",
                    "completion_record", record.Path,
                    "recorded_run", record.RunId,
                    "recorded_utc", record.TimestampUtc,
                    "inputs", record.Inputs.Count.ToString()),
                data: new { record.Path, Inputs = record.Inputs, Outputs = record.OutputVariables });

            return;
        }

        Execute(step, target, iterationOrder, state, index);
    }

    private void Execute(
        Step step,
        string? target,
        IReadOnlyList<string> iterationOrder,
        ProjectState state,
        ArtifactIndex index)
    {
        var inputs = reconciler.ComputeInputs(step, target, state, index);
        var supersedeReason = SupersedeReasonFor(step, target);

        var inFlight = new InFlight(
            step.Id,
            target,
            RunIdentity.TimestampUtc(),
            [.. inputs.Select(i => new KeyValuePair<string, string>(i.Name, i.Hash))]);

        control.Current = inFlight;

        Emit.To(
            Surface.Console,
            EventKinds.StepStarted,
            $"{step.Id}{(target is null ? "" : $" [{target}]")} — {step.Description}",
            Emit.Fields(
                "step", step.Id,
                "iteration_target", target ?? "-",
                "template", step.PromptTemplate is null ? "-" : $"prompts/{step.PromptTemplate}",
                "inputs", inputs.Count.ToString(),
                "supersede_reason", supersedeReason),
            data: new { Step = step.Id, Target = target, Inputs = inputs, step.Description });

        var stopwatch = Stopwatch.StartNew();

        var context = new StepContext(
            step, target, iterationOrder, graph, reconciler, state, index,
            records, artifacts, llm, control, paths, promptsDirectory, inputs, supersedeReason,
            ConfirmDefectAndCascade);

        step.Execute(context);

        // A committing step orders its writes so the artifact lands before the record marking the
        // step complete. StepContext.WriteArtifact has already run; this is the record.
        // context.Inputs, not the list computed above: a decision step appends the answer record
        // it consumed, which could not exist before the step ran (feature 6.3).
        var completion = records.WriteCompletion(
            step.Id,
            target,
            context.Inputs,
            context.CommittedVariables,
            context.WrittenArtifacts,
            context.Summary.Length > 0 ? context.Summary : step.Description);

        stopwatch.Stop();
        control.Current = null;

        Emit.To(
            Surface.Console,
            EventKinds.StepCompleted,
            $"{step.Id}{(target is null ? "" : $" [{target}]")} — committed in {stopwatch.ElapsedMilliseconds} ms",
            Emit.Fields(
                "step", step.Id,
                "iteration_target", target ?? "-",
                "completion_record", completion.Path,
                "variables", context.CommittedVariables.Count.ToString(),
                "artifacts", context.WrittenArtifacts.Count.ToString(),
                "elapsed_ms", stopwatch.ElapsedMilliseconds.ToString()));
    }

    // ---- boundary requests -------------------------------------------------------------------

    /// <summary>
    /// Everything the operator can ask for, read exactly here: between steps, with nothing in
    /// flight. Feature 9.3 for stop, feature 1.13 for the state rebuild trigger, feature 8.8 for
    /// forced invalidation.
    /// </summary>
    private void HandleBoundaryRequests(ProjectState state, ArtifactIndex index)
    {
        if (control.TakeStateRebuildRequest())
        {
            var divergences = StateProjection.Diff(paths, state, index);
            StateProjection.Write(paths, state, index, records.Run);

            Emit.To(
                Surface.Console,
                EventKinds.StateRebuilt,
                divergences.Count == 0
                    ? "State rebuilt from artifacts and records; the file on disk already agreed."
                    : $"State rebuilt from artifacts and records; {divergences.Count} divergence(s) found:\n  "
                      + string.Join("\n  ", divergences),
                Emit.Fields("state_file", paths.Relative(paths.StateFile), "divergences", divergences.Count.ToString()));
        }

        while (control.TryTakeInvalidation(out var forced))
        {
            ApplyForcedInvalidation(forced);
            throw new HaltException(
                $"Operator-forced invalidation applied to '{forced.StepId}' and everything downstream of it. " +
                "Workflow position is derived, not stored, so the way to act on it is to run again: replay will " +
                "skip what is still in force and re-execute from the invalidated point.");
        }

        if (control.StopRequested)
        {
            throw new StopRequestedException();
        }

        control.Shutdown.Token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Feature 8.8 - "invalidate this step and everything downstream", writing an
    /// operator-forced invalidation record per the 1.7 cause taxonomy and triggering the same
    /// downstream cascade as any other invalidation. There is no second mechanism.
    /// </summary>
    public void ApplyForcedInvalidation(ForcedInvalidation forced)
    {
        if (!graph.Contains(forced.StepId))
        {
            throw new HaltException($"Cannot invalidate '{forced.StepId}': no step with that id exists.");
        }

        var affected = new List<string> { forced.StepId };
        affected.AddRange(graph.DownstreamClosure(forced.StepId));

        var written = 0;
        foreach (var stepId in affected)
        {
            var cause = stepId == forced.StepId ? InvalidationCause.OperatorForced : InvalidationCause.UpstreamCascade;
            var explanation = stepId == forced.StepId
                ? "The operator invalidated this step from the console. Unlike deleting the record by hand, this " +
                  "path leaves a recorded cause (features 1.9 and 8.8 are both sanctioned; only one of them explains itself)."
                : $"Reachable downstream of '{forced.StepId}', which the operator invalidated (feature 1.6).";

            IReadOnlyList<string?> targets = graph.ById(stepId).IteratesOver is null
                ? [forced.StepId == stepId ? forced.IterationTarget : null]
                : [.. records.RecordedTargets(stepId)];

            foreach (var target in targets)
            {
                var record = records.InForceCompletion(stepId, target);
                if (record is null)
                {
                    continue;
                }

                records.WriteInvalidation(
                    stepId, target, record.Path, cause, "(whole step)", record.Path, "-", "operator", explanation);
                written++;
            }
        }

        Emit.To(
            Surface.Console,
            EventKinds.RecordInvalidated,
            $"Operator-forced invalidation of '{forced.StepId}' wrote {written} invalidation record(s) across "
            + $"{affected.Count} step(s) in the downstream closure.",
            Emit.Fields(
                "step", forced.StepId,
                "iteration_target", forced.IterationTarget ?? "-",
                "cause", InvalidationCause.OperatorForced,
                "records_written", written.ToString(),
                "closure", string.Join(", ", affected)));
    }

    // ---- backward flow -----------------------------------------------------------------------

    /// <summary>
    /// Feature 7.3 - backward flow is the invalidation cascade, not a second mechanism.
    /// Confirming a defect invalidates the named upstream artifact's producing step and
    /// everything derived from it through the same code path as any other invalidation, then the
    /// run halts: workflow position is derived, so re-establishing the corrected chain is done by
    /// running again.
    ///
    /// Feature 7.4 - before doing that, the history of this exact upstream/downstream pair is
    /// counted. A pair that cannot converge is a genuine judgment the application cannot make.
    /// </summary>
    public void ConfirmDefectAndCascade(string upstreamStepId, string raisedByStepId, string findingArtifactPath)
    {
        var history = records.ReadAllInvalidations()
            .Where(i => i.StepId == upstreamStepId
                        && i.Cause == InvalidationCause.DefectConfirmed
                        && i.RaisedByStep == raisedByStepId)
            .ToList();

        if (history.Count >= RevisionPingPongThreshold)
        {
            var report = string.Join("\n", history.Select(h =>
                $"  {h.TimestampUtc}  run {h.RunId} seq {h.Sequence}  {h.Path}"));

            throw new HaltException(
                $"'{raisedByStepId}' has now invalidated '{upstreamStepId}' {history.Count} times, which is at or past " +
                $"the code-defined threshold of {RevisionPingPongThreshold}. An upstream/downstream pair that cannot " +
                "converge is a judgment this application does not make (feature 7.4).\n\nRevision history:\n" + report);
        }

        if (!graph.Contains(upstreamStepId))
        {
            throw new HaltException($"The defect finding names upstream step '{upstreamStepId}', which does not exist.");
        }

        var affected = new List<string> { upstreamStepId };
        affected.AddRange(graph.DownstreamClosure(upstreamStepId));

        foreach (var stepId in affected)
        {
            var cause = stepId == upstreamStepId ? InvalidationCause.DefectConfirmed : InvalidationCause.UpstreamCascade;
            IReadOnlyList<string?> targets = graph.ById(stepId).IteratesOver is null
                ? [null]
                : [.. records.RecordedTargets(stepId)];

            foreach (var target in targets)
            {
                var record = records.InForceCompletion(stepId, target);
                if (record is null)
                {
                    continue;
                }

                records.WriteInvalidation(
                    stepId, target, record.Path, cause, "(whole step)", record.Path, "-", raisedByStepId,
                    stepId == upstreamStepId
                        ? $"A person confirmed the defect finding at {findingArtifactPath}, raised by '{raisedByStepId}'."
                        : $"Reachable downstream of '{upstreamStepId}', whose defect was confirmed (feature 1.6).");
            }
        }

        Emit.To(
            Surface.Console,
            EventKinds.RecordInvalidated,
            $"Defect confirmed. '{upstreamStepId}' and {affected.Count - 1} downstream step(s) invalidated through the "
            + "ordinary cascade. Run again to re-establish the chain from the corrected source.",
            Emit.Fields(
                "upstream_step", upstreamStepId,
                "raised_by", raisedByStepId,
                "finding", findingArtifactPath,
                "cause", InvalidationCause.DefectConfirmed,
                "closure", string.Join(", ", affected)));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private void AssertNoIterationDrift(FrozenIterationList list, Step step)
    {
        var absolute = paths.Absolute(list.SourceArtifactPath);
        if (!File.Exists(absolute))
        {
            throw new HaltException(
                $"The artifact the iteration set was derived from ({list.SourceArtifactPath}) is no longer on disk. " +
                "The run stops rather than finishing against a list nobody can reconstruct (feature 5.2).")
            {
                StepId = step.Id
            };
        }

        var actual = Canonical.HashFile(absolute);
        if (actual == list.SourceHash)
        {
            return;
        }

        throw new HaltException(
            $"The source of the iteration set changed while iterating. {list.SourceArtifactPath} was " +
            $"{list.SourceHash} when the list was frozen and is {actual} now. The run stops here rather than " +
            "finishing against a list nobody can reconstruct (feature 5.2). The frozen list itself is intact at " +
            $"{list.ArtifactPath}; invalidate the step that froze it if the new content is what you want.")
        {
            StepId = step.Id
        };
    }

    private static bool GuardInputsAvailable(StepGuard guard, ProjectState state, string? target)
        => guard.ReadsVariables.All(n => state.Has(n) || state.Has(ProjectState.Namespaced(n, target)));

    private string SupersedeReasonFor(Step step, string? target)
    {
        var invalidations = records.ReadInvalidations(step.Id, target);
        if (invalidations.Count == 0)
        {
            return "-";
        }

        var latest = invalidations[^1];
        return $"{latest.Cause} ({latest.Path})";
    }

    /// <summary>
    /// Plan-lookup key for a step, optionally narrowed to one iteration target. The separator is
    /// a NUL because no step id or target identity can contain one, written as an escape so the
    /// source stays pure ASCII (see ArtifactIndex.NoTargetKey for the same reasoning).
    /// </summary>
    private static string Key(string stepId, string? target) => $"{stepId}\0{target ?? "-"}";
}
