using System.Collections.Concurrent;

namespace SpecRunner.Engine;

/// <summary>Feature 8.4 - the single thing in flight, always visible.</summary>
public sealed record InFlight(
    string StepId,
    string? IterationTarget,
    string StartedUtc,
    IReadOnlyList<KeyValuePair<string, string>> InputHashes);

/// <summary>A block the browser must render an answer form for (feature 6.1).</summary>
public sealed record PendingBlock(
    string StepId,
    string? IterationTarget,
    int QuestionVersion,
    string Question,
    IReadOnlyList<string> PermittedAnswers,
    string QuestionPath,
    string AnswerPath);

/// <summary>An operator-forced invalidation waiting to be applied at a step boundary (feature 8.8).</summary>
public sealed record ForcedInvalidation(string StepId, string? IterationTarget);

public enum RunPhase
{
    Reconciling,
    AwaitingStart,
    Running,
    Blocked,
    Stopped,
    Halted,
    Completed
}

/// <summary>
/// Everything the browser can ask the run to do, and everything it needs to render what the run
/// is doing. All of it is a flag or a queue read at a step boundary - never an interrupt.
///
/// Feature 9.3 - stop is a flag checked at step boundaries: the in-flight step runs to its
/// commit, a "stopped by operator" record is written, and the process exits. Stop never
/// interrupts a commit, because a commit half-applied is the one state the persistence design
/// is built to make impossible.
/// </summary>
public sealed class RunControl
{
    private readonly ConcurrentQueue<ForcedInvalidation> _forced = new();
    private int _stopRequested;
    private int _startRequested;
    private int _rebuildRequested;

    public CancellationTokenSource Shutdown { get; } = new();

    public volatile RunPhase Phase = RunPhase.Reconciling;

    public volatile InFlight? Current;

    public volatile PendingBlock? Block;

    public bool StopRequested => Volatile.Read(ref _stopRequested) == 1;

    public bool StartRequested => Volatile.Read(ref _startRequested) == 1;

    public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);

    public void RequestStart() => Interlocked.Exchange(ref _startRequested, 1);

    public void RequestStateRebuild() => Interlocked.Exchange(ref _rebuildRequested, 1);

    public bool TakeStateRebuildRequest() => Interlocked.Exchange(ref _rebuildRequested, 0) == 1;

    public void RequestInvalidation(ForcedInvalidation invalidation) => _forced.Enqueue(invalidation);

    public bool TryTakeInvalidation(out ForcedInvalidation invalidation) => _forced.TryDequeue(out invalidation!);

    public bool HasPendingInvalidations => !_forced.IsEmpty;
}
