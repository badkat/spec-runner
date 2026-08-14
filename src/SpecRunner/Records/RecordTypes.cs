using SpecRunner.Core;

namespace SpecRunner.Records;

/// <summary>
/// One thing a step consumed, named by content hash and never by timestamp (feature 1.4). File
/// mtimes are not consulted anywhere in this application.
/// </summary>
/// <param name="Kind">"file" or "variable".</param>
/// <param name="Name">Project-relative path for a file; variable name for a variable.</param>
/// <param name="Hash">Hash of the canonical form of the content or value.</param>
/// <param name="Producer">Step id that produced this, or a non-step origin such as "template".</param>
public sealed record InputRef(string Kind, string Name, string Hash, string Producer)
{
    public const string FileKind = "file";
    public const string VariableKind = "variable";

    public IReadOnlyList<KeyValuePair<string, string>> ToRow() =>
    [
        new("kind", Kind),
        new("name", Name),
        new("hash", Hash),
        new("producer", Producer)
    ];

    public static InputRef FromRow(IReadOnlyList<KeyValuePair<string, string>> row, string origin)
        => new(
            Field(row, "kind", origin),
            Field(row, "name", origin),
            Field(row, "hash", origin),
            Field(row, "producer", origin));

    internal static string Field(IReadOnlyList<KeyValuePair<string, string>> row, string key, string origin)
    {
        foreach (var entry in row)
        {
            if (entry.Key == key)
            {
                return entry.Value;
            }
        }

        throw new HaltException(
            $"Malformed record {origin}: a list entry is missing the required field '{key}'. " +
            "A malformed record is a halt, not something to skip past (feature 1.9).");
    }
}

/// <summary>
/// A variable a step committed. The value itself is stored, not just its hash, because feature
/// 1.13 requires project state to be reconstructable from artifacts and records alone - without
/// re-running anything and without the model being called again.
/// </summary>
public sealed record OutputVariable(string Name, string Value, string Hash)
{
    public static OutputVariable Of(string name, string value)
        => new(name, value, Canonical.HashValue(value));

    public IReadOnlyList<KeyValuePair<string, string>> ToRow() =>
    [
        new("name", Name),
        new("value", Value),
        new("hash", Hash)
    ];

    public static OutputVariable FromRow(IReadOnlyList<KeyValuePair<string, string>> row, string origin)
        => new(
            InputRef.Field(row, "name", origin),
            InputRef.Field(row, "value", origin),
            InputRef.Field(row, "hash", origin));
}

/// <summary>A specific version of a specific artifact, optionally scoped to an iteration target.</summary>
public sealed record ArtifactRef(string ArtifactId, string? IterationTarget, int Version, string Path, string Hash)
{
    public IReadOnlyList<KeyValuePair<string, string>> ToRow() =>
    [
        new("artifact_id", ArtifactId),
        new("iteration_target", IterationTarget ?? RecordStore.NoTarget),
        new("version", Version.ToString()),
        new("path", Path),
        new("hash", Hash)
    ];

    public static ArtifactRef FromRow(IReadOnlyList<KeyValuePair<string, string>> row, string origin)
    {
        var target = InputRef.Field(row, "iteration_target", origin);
        var version = InputRef.Field(row, "version", origin);
        if (!int.TryParse(version, out var parsed))
        {
            throw new HaltException($"Malformed record {origin}: artifact version '{version}' is not an integer.");
        }

        return new ArtifactRef(
            InputRef.Field(row, "artifact_id", origin),
            target == RecordStore.NoTarget ? null : target,
            parsed,
            InputRef.Field(row, "path", origin),
            InputRef.Field(row, "hash", origin));
    }
}

/// <summary>
/// Feature 1.7 - the cause taxonomy carried on every invalidation record. Invalidation is
/// additive: the superseded completion record stays on disk, and there is no code path anywhere
/// in this application that deletes a file.
/// </summary>
public static class InvalidationCause
{
    /// <summary>An input this step consumed has a different hash than the record named. Stale.</summary>
    public const string InputHashMismatch = "input-hash-mismatch";

    /// <summary>
    /// Feature 1.8 - an artifact whose body no longer matches the hash recorded in its own origin
    /// header was edited by a person. Reported differently from staleness because it means
    /// something different to the operator, though both accept the on-disk content as truth.
    /// </summary>
    public const string ArtifactHandEdited = "artifact-hand-edited";

    /// <summary>Feature 8.8 - the operator pressed the button.</summary>
    public const string OperatorForced = "operator-forced";

    /// <summary>Feature 1.6 - reachable downstream of something else that was invalidated.</summary>
    public const string UpstreamCascade = "upstream-cascade";

    /// <summary>Feature 1.5 - the canonicalization rules changed, so every hash on disk is unreadable.</summary>
    public const string CanonicalizationVersionBump = "canonicalization-version-bump";

    /// <summary>
    /// Features 7.2 / 7.3 - a downstream validator suspected an upstream defect and a person
    /// confirmed it. Backward flow is this cause travelling through the ordinary cascade; there
    /// is no separate rewind engine.
    /// </summary>
    public const string DefectConfirmed = "defect-confirmed";

    /// <summary>Feature 1.9 - the record file the operator deleted by hand is simply absent.</summary>
    public const string RecordAbsent = "record-absent";

    public static readonly IReadOnlyList<string> All =
    [
        InputHashMismatch,
        ArtifactHandEdited,
        OperatorForced,
        UpstreamCascade,
        CanonicalizationVersionBump,
        DefectConfirmed,
        RecordAbsent
    ];
}

/// <summary>A step's committed completion. Its presence is what makes the step skippable.</summary>
public sealed record CompletionRecord(
    string Path,
    string StepId,
    string? IterationTarget,
    string RunId,
    int Sequence,
    string TimestampUtc,
    int CanonicalizationVersion,
    IReadOnlyList<InputRef> Inputs,
    IReadOnlyList<OutputVariable> OutputVariables,
    IReadOnlyList<ArtifactRef> OutputArtifacts)
{
    /// <summary>Ordering key. Run ids lead with a UTC timestamp, so this sorts chronologically.</summary>
    public (string, int) Order => (RunId, Sequence);
}

/// <summary>An invalidation, naming what it superseded and exactly why.</summary>
public sealed record InvalidationRecord(
    string Path,
    string StepId,
    string? IterationTarget,
    string TargetRecordPath,
    string Cause,
    string DifferingInput,
    string ExpectedHash,
    string ActualHash,
    string RaisedByStep,
    string RunId,
    int Sequence,
    string TimestampUtc);

/// <summary>A pending human decision. Version n is answered by answer.v&lt;n&gt;.md beside it.</summary>
public sealed record QuestionRecord(
    string Path,
    string StepId,
    string? IterationTarget,
    int Version,
    string Question,
    IReadOnlyList<string> PermittedAnswers,
    string CodeLocation,
    IReadOnlyList<InputRef> Inputs,
    IReadOnlyList<string> RelatedArtifacts,
    string RunId,
    int Sequence,
    string TimestampUtc);

/// <summary>An answer to one version of one question.</summary>
public sealed record AnswerRecord(
    string Path,
    string StepId,
    string? IterationTarget,
    int Version,
    string Answer,
    string Surface,
    string TimestampUtc)
{
    /// <summary>Feature 6.4 - the surface marker, distinguishing the two channels 6.1 permits.</summary>
    public const string SurfaceBrowser = "browser";

    public const string SurfaceHandWritten = "hand-written";

    public static readonly IReadOnlyList<string> Surfaces = [SurfaceBrowser, SurfaceHandWritten];
}
