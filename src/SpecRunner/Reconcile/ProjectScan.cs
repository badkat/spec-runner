using SpecRunner.Core;

namespace SpecRunner.Reconcile;

/// <summary>A file in the project tree the workflow does not recognise, with why it was rejected.</summary>
public sealed record UnrecognizedFile(string RelativePath, string Reason);

/// <summary>An artifact on disk that no honored record names, and the run that likely wrote it.</summary>
public sealed record OrphanedArtifact(string RelativePath, string LikelyRunId, string ProducingStepId);

/// <summary>
/// Feature 1.12 - unrecognized files in the project tree halt the run, with a fixed allowlist
/// for names and directories the workflow doesn't own.
///
/// Every unrecognized file found in this pass is reported at once, not just the first: the
/// operator should see the complete set before doing anything about any of them. Resolution is
/// manual and out-of-band - there is no "acknowledge and keep" path, no decision record for this
/// case, and no per-run or config-driven exception mechanism.
///
/// An empty project directory is a valid startup state. The workflow's own earliest steps are
/// responsible for creating the project's starting files.
/// </summary>
public static class ProjectScan
{
    public static IReadOnlyList<UnrecognizedFile> FindUnrecognizedFiles(ProjectPaths paths)
    {
        var findings = new List<UnrecognizedFile>();
        if (!Directory.Exists(paths.Root))
        {
            return findings;
        }

        Walk(paths.Root, paths, findings);
        return [.. findings.OrderBy(f => f.RelativePath, StringComparer.Ordinal)];
    }

    private static void Walk(string directory, ProjectPaths paths, List<UnrecognizedFile> findings)
    {
        foreach (var file in Directory.GetFiles(directory))
        {
            var relative = paths.Relative(file);
            var name = Path.GetFileName(file);

            // The sanctioned operator-note convention: a fixed filename pattern the workflow
            // always ignores and never reads as input.
            if (name.EndsWith(ProjectPaths.NoteFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extension matching is case-insensitive; anything without a .md extension is not a
            // recognized artifact regardless of its content.
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new UnrecognizedFile(relative,
                    "not a .md file - the project contains only Markdown the workflow produced"));
                continue;
            }

            var top = TopLevelSegment(relative);
            if (top is null)
            {
                findings.Add(new UnrecognizedFile(relative,
                    "a .md file at the project root; the workflow writes only into " +
                    string.Join(", ", ProjectPaths.OwnedDirectories.Select(d => d + "/"))));
                continue;
            }

            if (!ProjectPaths.OwnedDirectories.Contains(top))
            {
                findings.Add(new UnrecognizedFile(relative,
                    $"under '{top}/', which is not a directory the workflow owns"));
            }
        }

        foreach (var child in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(child);

            // The static, checked-in allowlist for directories the workflow doesn't own.
            if (ProjectPaths.AllowedForeignDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // The sanctioned operator-note subfolder: ignored entirely, never descended into.
            if (string.Equals(name, ProjectPaths.NotesDirectoryName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFullPath(child), Path.GetFullPath(paths.Notes), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Walk(child, paths, findings);
        }
    }

    private static string? TopLevelSegment(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        return separator < 0 ? null : relativePath[..separator];
    }
}
