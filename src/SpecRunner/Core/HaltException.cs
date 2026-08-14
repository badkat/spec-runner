namespace SpecRunner.Core;

/// <summary>
/// Pillar 3 - explicit failure. Every condition the application does not know how to proceed
/// through throws this. There is no catch that continues; the only handlers are the ones that
/// flush both surfaces and end the process (9.5), or the runner boundary that records the halt
/// to disk before rethrowing.
///
/// A halt is not an error code and not a status - it is the end of the run.
/// </summary>
public sealed class HaltException : Exception
{
    public HaltException(string message) : base(message)
    {
    }

    public HaltException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Step id in flight when the halt occurred, when there was one.</summary>
    public string? StepId { get; init; }

    /// <summary>Iteration target in flight when the halt occurred, when there was one.</summary>
    public string? IterationTarget { get; init; }
}
