namespace SpecRunner.Workflow;

/// <summary>
/// The one methodology, hardcoded.
///
/// There is no manifest format, no DSL, no plugin registry, and no user-authored or user-reordered
/// workflow. This list, in this order, is the methodology; the engine around it exists to run
/// exactly this and nothing else.
///
/// ---------------------------------------------------------------------------------------------
/// PLACEHOLDER. The real phase/task/step breakdown is not yet written. What is below is a small
/// skeleton whose purpose is to prove the engine runs and to exercise each mechanism the feature
/// list requires exactly once - an LLM step, a frozen iteration set, a human decision block, a
/// verdict branch, a defect finding, and backward flow through the invalidation cascade - so that
/// none of that machinery sits untested behind a workflow that never reaches it.
///
/// Replacing it means rewriting this list and the classes in MethodologySteps.cs. Nothing else in
/// the application knows what these steps are: the graph is built from their declarations, and
/// every id here is an explicit constant, so renaming one is a deliberate act that invalidates
/// that step and its descendants (feature 1.3).
/// ---------------------------------------------------------------------------------------------
/// </summary>
public static class Methodology
{
    public static IReadOnlyList<Step> Steps() =>
    [
        // Phase 1 - intake. The workflow creates the project's own starting files (feature 1.12),
        // then stops and asks a person whether the brief is actually ready.
        new SeedBriefStep(),
        new ConfirmBriefStep(),

        // Phase 1, task 2 - the first model call, and the frozen iteration set derived from it.
        new DeriveRequirementsStep(),
        new FreezeRequirementSetStep(),

        // Phase 2 - one specification per frozen requirement, then a downstream validator whose
        // verdict is a machine-checkable enum and whose prose code never reads.
        new SpecifyRequirementStep(),
        new ValidateSpecsStep(),
        new RouteVerdictStep(),

        // Phase 2, task 2 - upstream precedence. A suspected defect becomes an artifact and a
        // block; only a confirmed one becomes an invalidation cascade.
        new RaiseDefectFindingStep(),
        new ConfirmDefectStep(),
        new ApplyBackwardFlowStep(),

        // Phase 3 - the account of what this run established.
        new FinalReportStep()
    ];
}
