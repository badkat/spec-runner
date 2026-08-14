using System.Text;
using SpecRunner.Core;
using SpecRunner.State;

namespace SpecRunner.Reconcile;

/// <summary>
/// Feature 1.13 - the project state file is a projection, rebuilt and diffed every startup.
///
/// State is always reconstructed from artifacts and records. The file on disk is then compared
/// to the reconstruction and any divergence reported field by field before the run begins. The
/// file is a convenience for human reading; it is never an input to execution, and nothing
/// anywhere reads a value back out of it.
/// </summary>
public static class StateProjection
{
    /// <summary>Compares what is on disk to the reconstruction, field by field. Never repairs silently.</summary>
    public static IReadOnlyList<string> Diff(ProjectPaths paths, ProjectState reconstructed, ArtifactIndex artifacts)
    {
        if (!File.Exists(paths.StateFile))
        {
            return reconstructed.Values.Count == 0 && artifacts.All().Count == 0
                ? []
                : ["state/project-state.md does not exist; it will be written from the reconstruction."];
        }

        var previous = MdDoc.Parse(File.ReadAllText(paths.StateFile), "state/project-state.md");
        var divergences = new List<string>();

        var onDisk = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in previous.RequireMapList("variables", "state/project-state.md"))
        {
            onDisk[Records.InputRef.Field(row, "name", "state/project-state.md")] =
                Records.InputRef.Field(row, "hash", "state/project-state.md");
        }

        foreach (var (name, value) in reconstructed.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (!onDisk.TryGetValue(name, out var hash))
            {
                divergences.Add($"variable '{name}': absent from the state file, present in the reconstruction ({value.Hash}).");
            }
            else if (hash != value.Hash)
            {
                divergences.Add($"variable '{name}': state file says {hash}, reconstruction says {value.Hash}.");
            }
        }

        foreach (var name in onDisk.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!reconstructed.Has(name))
            {
                divergences.Add($"variable '{name}': present in the state file, absent from the reconstruction.");
            }
        }

        return divergences;
    }

    public static void Write(ProjectPaths paths, ProjectState state, ArtifactIndex artifacts, RunIdentity run)
    {
        var doc = new MdDoc()
            .Set("record_kind", "state-projection")
            .Set("run_id", run.Id)
            .Set("timestamp_utc", RunIdentity.TimestampUtc())
            .Set("hash_algorithm", Canonical.HashAlgorithm)
            .Set("canonicalization_version", Canonical.Version)
            .SetMapList("variables", state.Values
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => new List<KeyValuePair<string, string>>
                {
                    new("name", v.Value.Name),
                    new("hash", v.Value.Hash),
                    new("producer", v.Value.ProducerStepId)
                }))
            .SetMapList("artifacts", artifacts.All().Select(a => new List<KeyValuePair<string, string>>
            {
                new("artifact_id", a.ArtifactId),
                new("iteration_target", a.IterationTarget ?? "-"),
                new("version", a.Version.ToString()),
                new("path", a.Path),
                new("hash", a.Hash)
            }));

        var body = new StringBuilder();
        body.Append(
            """
            This file is a projection, rebuilt from artifacts and records on every startup. It is a
            convenience for reading, never an input to execution: editing it changes nothing, and the
            next run will overwrite it with whatever the records actually say.

            ## Variables

            """);

        if (state.Values.Count == 0)
        {
            body.Append("_none committed_\n");
        }
        else
        {
            foreach (var value in state.Values.Values.OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                body.Append($"### `{value.Name}`\n\n")
                    .Append($"Committed by `{value.ProducerStepId}` — `{value.Hash}`\n\n")
                    .Append("```\n")
                    .Append(Abbreviate(value.Value))
                    .Append("\n```\n\n");
            }
        }

        body.Append("## Artifacts in force\n\n");
        var all = artifacts.All();
        if (all.Count == 0)
        {
            body.Append("_none_\n");
        }
        else
        {
            foreach (var reference in all)
            {
                body.Append($"- `{reference.Path}` — {reference.ArtifactId} v{reference.Version}")
                    .Append(reference.IterationTarget is null ? "" : $" (target `{reference.IterationTarget}`)")
                    .Append('\n');
            }
        }

        doc.Body = body.ToString();
        Directory.CreateDirectory(paths.State);
        AtomicFile.WriteProjection(paths.StateFile, doc.Serialize());
    }

    private static string Abbreviate(string value)
        => value.Length <= 2000 ? value : value[..2000] + $"\n... ({value.Length - 2000} more characters)";
}
