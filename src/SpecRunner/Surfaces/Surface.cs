namespace SpecRunner.Surfaces;

/// <summary>
/// Pillar 2 names exactly two surfaces and rejects diagnostics going anywhere else. This enum is
/// the whole set; there is no third member and no "both" member, because a class of information
/// that belongs to two surfaces belongs to neither.
/// </summary>
public enum Surface
{
    /// <summary>
    /// Feature 8.9 - the terminal owns pre-console and process-ending events only: startup
    /// self-checks, config resolution, port binding, unhandled exceptions, shutdown reason.
    /// Nothing else. In particular it does not echo workflow state, and it does not echo a
    /// mid-run block (feature 6.5).
    /// </summary>
    Terminal,

    /// <summary>
    /// The browser console owns workflow state: the pre-flight plan, per-step execution,
    /// skips, invalidations, model output, blocks, and halts that occur once the server is up.
    /// Everything sent here is simultaneously appended to the run log on disk (feature 8.2).
    /// </summary>
    Console
}

/// <summary>
/// Event kinds. Named constants rather than free-form strings so the browser can style them and
/// so an operator grepping the run log has a stable vocabulary to grep for.
/// </summary>
public static class EventKinds
{
    // Terminal - startup and shutdown.
    public const string Startup = "startup";
    public const string Config = "config";
    public const string SelfCheck = "self-check";
    public const string GraphValidation = "graph-validation";
    public const string StartupBlock = "startup-block";
    public const string PortBinding = "port-binding";
    public const string BrowserLaunch = "browser-launch";
    public const string Shutdown = "shutdown";
    public const string Fatal = "fatal";

    // Console - reconciliation.
    public const string ReconcileStarted = "reconcile-started";
    public const string RecordHonored = "record-honored";
    public const string RecordInvalidated = "record-invalidated";
    public const string OrphanArtifact = "orphan-artifact";
    public const string StateDiff = "state-diff";
    public const string StateRebuilt = "state-rebuilt";
    public const string Plan = "plan";
    public const string PlanGate = "plan-gate";

    // Console - execution.
    public const string RunStarted = "run-started";
    public const string StepStarted = "step-started";
    public const string StepSkipped = "step-skipped";
    public const string StepNotApplicable = "step-not-applicable";
    public const string StepCompleted = "step-completed";
    public const string ArtifactWritten = "artifact-written";
    public const string StepDetail = "step-detail";
    public const string IterationFrozen = "iteration-frozen";
    public const string IterationItem = "iteration-item";
    public const string Decision = "decision";
    public const string Block = "block";
    public const string BlockResolved = "block-resolved";
    public const string DefectFinding = "defect-finding";

    // Console - model calls.
    public const string LlmRequest = "llm-request";
    public const string LlmAttempt = "llm-attempt";
    public const string LlmToken = "llm-token";
    public const string LlmResponse = "llm-response";
    public const string LlmCondition = "llm-condition";

    // Console - end of run.
    public const string RunStopped = "run-stopped";
    public const string RunHalted = "run-halted";
    public const string RunCompleted = "run-completed";
}
