using SpecRunner.Core;
using SpecRunner.Records;

namespace SpecRunner.Web;

/// <summary>One node of an artifact's origin chain, as rendered for the browser.</summary>
public sealed record ProvenanceNode(
    string Path,
    string ArtifactId,
    string Version,
    string ProducingStepId,
    string IterationTarget,
    string RunId,
    string TimestampUtc,
    string ModelRequested,
    string ModelReported,
    string PromptTemplatePath,
    string ResolvedPromptHash,
    string SupersedesVersion,
    string SupersededBecause,
    string DeclaredBodyHash,
    string ActualBodyHash,
    bool HandEdited,
    IReadOnlyList<ProvenanceEdge> Inputs);

/// <summary>
/// A parent reference: relative path plus hash (feature 2.2), plus whether the hash still holds.
/// A parent whose content has moved on is the single most useful thing this view can show.
/// </summary>
public sealed record ProvenanceEdge(
    string Kind,
    string Name,
    string RecordedHash,
    string CurrentHash,
    bool Matches,
    string Producer,
    ProvenanceNode? Parent);

/// <summary>
/// Feature 8.7 - "explain this artifact": render its full origin chain to roots.
///
/// This is the convenience view. The same information is hand-traversable on disk with nothing
/// but a text editor, which is the property Pillar 9 actually requires; this walker exists
/// because doing it by hand for a deep chain is tedious, not because doing it by hand is
/// impossible.
/// </summary>
public static class ProvenanceWalker
{
    private const int MaxDepth = 32;

    public static ProvenanceNode Explain(ArtifactStore artifacts, ProjectPaths paths, string relativePath)
        => Walk(artifacts, paths, relativePath, 0, new HashSet<string>(StringComparer.Ordinal));

    private static ProvenanceNode Walk(
        ArtifactStore artifacts,
        ProjectPaths paths,
        string relativePath,
        int depth,
        HashSet<string> seen)
    {
        if (depth > MaxDepth)
        {
            throw new HaltException(
                $"The origin chain from {relativePath} is more than {MaxDepth} deep. The dependency graph is acyclic " +
                "by construction (feature 1.2), so a chain this long means an artifact's recorded inputs do not " +
                "match the graph that produced them.");
        }

        var artifact = artifacts.Read(relativePath);
        var header = artifact.Header;

        var edges = new List<ProvenanceEdge>();
        foreach (var row in header.RequireMapList("inputs", relativePath))
        {
            var input = InputRef.FromRow(row, relativePath);
            var current = "(not on disk)";
            ProvenanceNode? parent = null;

            if (input.Kind == InputRef.FileKind)
            {
                var absolute = input.Name.StartsWith("prompts/", StringComparison.Ordinal)
                    ? null
                    : paths.Absolute(input.Name);

                if (absolute is not null && File.Exists(absolute))
                {
                    current = Canonical.HashFile(absolute);

                    if (input.Name.StartsWith("artifacts/", StringComparison.Ordinal) && seen.Add(input.Name))
                    {
                        parent = Walk(artifacts, paths, input.Name, depth + 1, seen);
                    }
                }
                else if (absolute is null)
                {
                    current = "(outside the project tree)";
                }
            }
            else
            {
                current = "(a variable; its value lives in the producing step's completion record)";
            }

            edges.Add(new ProvenanceEdge(
                input.Kind,
                input.Name,
                input.Hash,
                current,
                current == input.Hash,
                input.Producer,
                parent));
        }

        return new ProvenanceNode(
            relativePath,
            header.Require("artifact_id", relativePath),
            header.Require("artifact_version", relativePath),
            header.Require("producing_step_id", relativePath),
            header.Require("iteration_target", relativePath),
            header.Require("run_id", relativePath),
            header.Require("timestamp_utc", relativePath),
            header.Require("model_requested", relativePath),
            header.Require("model_reported", relativePath),
            header.Require("prompt_template_path", relativePath),
            header.Require("resolved_prompt_hash", relativePath),
            header.Require("supersedes_version", relativePath),
            header.Require("superseded_because", relativePath),
            artifact.DeclaredBodyHash,
            artifact.ActualBodyHash,
            artifact.WasHandEdited,
            edges);
    }
}
