using SpecRunner.Records;
using SpecRunner.State;

namespace SpecRunner.Reconcile;

/// <summary>What the pre-flight decided will happen to one step (feature 1.10).</summary>
public enum StepAction
{
    /// <summary>An in-force completion record was honored and its inputs still match.</summary>
    Skip,

    /// <summary>No honored record, or one that was invalidated in this pass.</summary>
    Execute,

    /// <summary>A code-defined guard evaluated false, so the step does not apply to this run.</summary>
    NotApplicable
}

/// <summary>One row of the pre-flight plan.</summary>
public sealed record PlannedStep(
    string StepId,
    string? IterationTarget,
    string Description,
    StepAction Action,
    string Reason,
    bool CallsModel,
    string RecordPath,
    IReadOnlyList<InputRef> Inputs)
{
    /// <summary>
    /// True when the row stands for an iterating step whose frozen list is not yet on disk, so
    /// the per-item rows cannot be enumerated until the producing step runs. Reported honestly
    /// rather than guessed at.
    /// </summary>
    public bool ItemsPending { get; init; }
}

/// <summary>
/// The complete result of reconciliation, rendered to the console before anything runs and
/// written to disk as part of the run log.
/// </summary>
public sealed record ReconcileResult(
    IReadOnlyList<PlannedStep> Plan,
    int DefiniteModelCalls,
    bool ModelCallCountIsLowerBound,
    ProjectState State,
    ArtifactIndex Artifacts,
    IReadOnlyList<InvalidationRecord> Invalidations,
    IReadOnlyList<OrphanedArtifact> Orphans,
    IReadOnlyList<string> HandEditedArtifacts,
    IReadOnlyList<string> IncompleteModelCalls,
    IReadOnlyList<StartupBlock> StartupBlocks,
    IReadOnlyList<string> StateDivergences);

/// <summary>
/// Feature 6.5 - a block discovered at startup, before the console can exist. Reported by the
/// terminal, in the same bucket as other startup self-checks.
/// </summary>
public sealed record StartupBlock(string StepId, string? IterationTarget, string QuestionPath, string Question);
