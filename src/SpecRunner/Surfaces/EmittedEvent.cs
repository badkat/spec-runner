namespace SpecRunner.Surfaces;

/// <summary>
/// One thing that happened, addressed to exactly one surface. This is the only shape in which
/// the application says anything to anyone.
/// </summary>
public sealed record EmittedEvent(
    int Sequence,
    string TimestampUtc,
    Surface Surface,
    string Kind,
    string Message,
    IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    /// <summary>
    /// Optional structured payload for the browser to render (a plan table, a step detail, a
    /// provenance chain). Never used by the terminal, which is line-oriented by nature.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// True for high-frequency display-only events - model tokens - which stream live to the
    /// browser but are folded into a single aggregated event in the run log. See RunLog for why.
    /// </summary>
    public bool TransientDisplayOnly { get; init; }
}
