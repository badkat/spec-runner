using SpecRunner.Core;
using SpecRunner.Records;

namespace SpecRunner.Workflow;

/// <summary>One entry in a frozen iteration list. Ordinal is data, never a directory name.</summary>
public sealed record IterationItem(int Ordinal, string Identity, string Text);

/// <summary>
/// Feature 5.1 - the iteration set is materialized to disk and frozen before the first item runs:
/// an ordered, numbered list artifact with its own origin header. Iteration proceeds against
/// that frozen list, never against a live re-scan of the directory or of state.
///
/// The list also carries the identity and hash of the artifact it was derived from, which is
/// what makes feature 5.2's drift check at each item boundary possible.
/// </summary>
public sealed record FrozenIterationList(
    string ArtifactPath,
    string SourceArtifactPath,
    string SourceHash,
    IReadOnlyList<IterationItem> Items)
{
    public IReadOnlyList<string> Identities => [.. Items.Select(i => i.Identity)];

    public static MdDoc Describe(
        MdDoc doc,
        string sourceArtifactPath,
        string sourceHash,
        IReadOnlyList<IterationItem> items)
    {
        doc.Set("iteration_source_path", sourceArtifactPath)
           .Set("iteration_source_hash", sourceHash)
           .SetMapList("iteration_items", items.Select(i => new List<KeyValuePair<string, string>>
           {
               new("ordinal", i.Ordinal.ToString()),
               new("identity", i.Identity),
               new("text", i.Text)
           }));
        return doc;
    }

    public static string RenderBody(IReadOnlyList<IterationItem> items, string sourceArtifactPath, string sourceHash)
    {
        var lines = items.Select(i => $"{i.Ordinal}. `{i.Identity}` — {i.Text}");
        return
            $"""
             Frozen at the moment this artifact was written. Iteration runs against this list and
             nothing else; the underlying set is re-hashed at every item boundary and a change
             halts the run rather than silently finishing against a list nobody can reconstruct.

             Derived from `{sourceArtifactPath}` at `{sourceHash}`.

             {string.Join("\n", lines)}
             """;
    }

    public static FrozenIterationList Read(StoredArtifact artifact)
    {
        var origin = artifact.RelativePath;
        var rows = artifact.Header.RequireMapList("iteration_items", origin);

        var items = new List<IterationItem>(rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var ordinalText = InputRef.Field(row, "ordinal", origin);
            if (!int.TryParse(ordinalText, out var ordinal))
            {
                throw new HaltException($"Frozen iteration list {origin} has a non-integer ordinal '{ordinalText}'.");
            }

            var identity = InputRef.Field(row, "identity", origin);
            if (!seen.Add(identity))
            {
                throw new HaltException(
                    $"Frozen iteration list {origin} contains the identity '{identity}' twice. " +
                    "Identities key the per-target record directories (section 10, decision 2) and must be unique.");
            }

            if (ordinal != items.Count + 1)
            {
                throw new HaltException(
                    $"Frozen iteration list {origin} is not contiguously numbered from 1: " +
                    $"expected ordinal {items.Count + 1}, found {ordinal}.");
            }

            items.Add(new IterationItem(ordinal, identity, InputRef.Field(row, "text", origin)));
        }

        if (items.Count == 0)
        {
            throw new HaltException($"Frozen iteration list {origin} contains no items.");
        }

        return new FrozenIterationList(
            origin,
            artifact.Header.Require("iteration_source_path", origin),
            artifact.Header.Require("iteration_source_hash", origin),
            items);
    }
}
