using SpecRunner.Core;

namespace SpecRunner.Records;

/// <summary>
/// Feature 2.1 - the origin header carried inside every artifact, not only in a sidecar.
///
/// Pillar 9's test is whether an unexpected result can be explained by reading *it* - so a
/// person holding one file open in an editor must not need to go hunting. Fields that do not
/// apply (an artifact written by a step that never called a model) are written as "-" rather
/// than omitted, so the shape is the same everywhere and a reader is never left wondering
/// whether a missing line means "not applicable" or "not recorded".
/// </summary>
public sealed record ArtifactOrigin
{
    public required string ArtifactId { get; init; }

    public required int Version { get; init; }

    public required string ProducingStepId { get; init; }

    public string? IterationTarget { get; init; }

    public required string RunId { get; init; }

    public required int Sequence { get; init; }

    public required string TimestampUtc { get; init; }

    /// <summary>Feature 2.2 - parent references are relative path plus hash, so the chain is hand-walkable.</summary>
    public required IReadOnlyList<InputRef> Inputs { get; init; }

    // ---- prompt provenance (feature 4.3) ----
    public string PromptTemplatePath { get; init; } = "-";

    public string PromptTemplateHash { get; init; } = "-";

    public string ResolvedPromptHash { get; init; } = "-";

    // ---- call configuration, exactly as the template declared it (feature 3.1) ----
    public string ModelRequested { get; init; } = "-";

    public string ModelReported { get; init; } = "-";

    public string Temperature { get; init; } = "-";

    public string TopP { get; init; } = "-";

    public string MaxTokens { get; init; } = "-";

    public string Seed { get; init; } = "-";

    // ---- determinism evidence (feature 3.7) ----
    public string ResponseId { get; init; } = "-";

    public string SystemFingerprint { get; init; } = "-";

    public string UsagePromptTokens { get; init; } = "-";

    public string UsageCompletionTokens { get; init; } = "-";

    public string UsageTotalTokens { get; init; } = "-";

    public string RawResponseRecord { get; init; } = "-";

    // ---- revision lineage (feature 7.5) ----
    public string SupersedesVersion { get; init; } = "-";

    public string SupersededBecause { get; init; } = "-";
}

/// <summary>
/// Reads and writes versioned artifacts. Nothing here overwrites: a re-produced artifact is a
/// new version beside the old one, and the old one stays readable forever (Pillar 7).
/// </summary>
public sealed class ArtifactStore(ProjectPaths paths)
{
    public ProjectPaths Paths { get; } = paths;

    /// <summary>Next unused version number for an artifact, by scanning what is on disk.</summary>
    public int NextVersion(string artifactId, string? iterationTarget)
        => ExistingVersions(artifactId, iterationTarget).DefaultIfEmpty(0).Max() + 1;

    public IReadOnlyList<int> ExistingVersions(string artifactId, string? iterationTarget)
    {
        var directory = Paths.ArtifactDirectory(artifactId, iterationTarget);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var versions = new List<int>();
        foreach (var file in Directory.GetFiles(directory, "v*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name.AsSpan(1), out var version))
            {
                versions.Add(version);
            }
        }

        versions.Sort();
        return versions;
    }

    /// <summary>
    /// Writes a new artifact version. The body is canonicalized first and its hash recorded in
    /// the artifact's own header - that self-reference is what lets feature 1.8 tell a
    /// hand-edited artifact from a merely stale one on the next run.
    /// </summary>
    public ArtifactRef Write(ArtifactOrigin origin, string body, Action<MdDoc>? extraHeader = null)
    {
        var canonicalBody = Canonical.Text(body);
        var bodyHash = Canonical.Hash(canonicalBody);

        var doc = new MdDoc()
            .Set("artifact_id", origin.ArtifactId)
            .Set("artifact_version", origin.Version)
            .Set("producing_step_id", origin.ProducingStepId)
            .Set("iteration_target", origin.IterationTarget ?? RecordStore.NoTarget)
            .Set("run_id", origin.RunId)
            .Set("sequence", origin.Sequence)
            .Set("timestamp_utc", origin.TimestampUtc)
            .Set("hash_algorithm", Canonical.HashAlgorithm)
            .Set("canonicalization_version", Canonical.Version)
            .Set("prompt_template_path", origin.PromptTemplatePath)
            .Set("prompt_template_hash", origin.PromptTemplateHash)
            .Set("resolved_prompt_hash", origin.ResolvedPromptHash)
            .Set("model_requested", origin.ModelRequested)
            .Set("model_reported", origin.ModelReported)
            .Set("temperature", origin.Temperature)
            .Set("top_p", origin.TopP)
            .Set("max_tokens", origin.MaxTokens)
            .Set("seed", origin.Seed)
            .Set("response_id", origin.ResponseId)
            .Set("system_fingerprint", origin.SystemFingerprint)
            .Set("usage_prompt_tokens", origin.UsagePromptTokens)
            .Set("usage_completion_tokens", origin.UsageCompletionTokens)
            .Set("usage_total_tokens", origin.UsageTotalTokens)
            .Set("raw_response_record", origin.RawResponseRecord)
            .Set("supersedes_version", origin.SupersedesVersion)
            .Set("superseded_because", origin.SupersededBecause)
            .SetMapList("inputs", origin.Inputs.Select(i => i.ToRow()));

        // Artifact kinds that carry structured header data of their own - the frozen iteration
        // list (feature 5.1) is the one the methodology uses - add it here, before body_hash, so
        // the hash stays the last line of the header wherever a reader looks.
        extraHeader?.Invoke(doc);

        doc.Set("body_hash", bodyHash);
        doc.Body = canonicalBody;

        var path = Paths.ArtifactFile(origin.ArtifactId, origin.IterationTarget, origin.Version);
        AtomicFile.WriteNew(path, doc.Serialize());

        var relative = Paths.Relative(path);
        return new ArtifactRef(origin.ArtifactId, origin.IterationTarget, origin.Version, relative, Canonical.HashFile(path));
    }

    public StoredArtifact Read(string relativePath)
    {
        var absolute = Paths.Absolute(relativePath);
        if (!File.Exists(absolute))
        {
            throw new HaltException($"Artifact {relativePath} is named by a record but is not on disk.");
        }

        var content = File.ReadAllText(absolute);
        var doc = MdDoc.Parse(content, relativePath);
        var (_, rawBody) = MdDoc.SplitRaw(content, relativePath);

        return new StoredArtifact(
            relativePath,
            doc,
            rawBody,
            doc.Require("body_hash", relativePath),
            Canonical.Hash(rawBody),
            Canonical.Hash(content));
    }
}

/// <summary>
/// An artifact as it currently sits on disk, with both the hash its own header claims and the
/// hash its body actually has. When those two disagree, a person edited it (feature 1.8).
/// </summary>
public sealed record StoredArtifact(
    string RelativePath,
    MdDoc Header,
    string Body,
    string DeclaredBodyHash,
    string ActualBodyHash,
    string FileHash)
{
    public bool WasHandEdited => DeclaredBodyHash != ActualBodyHash;
}
