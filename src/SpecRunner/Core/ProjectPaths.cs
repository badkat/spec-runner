using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecRunner.Core;

/// <summary>
/// Every path the application touches inside the project directory, in one place.
///
/// The layout is the navigation model. Feature 2.6 requires records to live in a directory tree
/// mirroring step ids, so an operator navigates records the same way they navigate the workflow;
/// section 10's second decision extends that one level deeper with a per-target subdirectory for
/// iterating steps, named by the target's own stable identifier rather than its ordinal position.
///
///   &lt;project&gt;/
///     artifacts/&lt;artifact-id&gt;/[&lt;target&gt;/]v&lt;n&gt;.md
///     records/&lt;phase&gt;/&lt;task&gt;/&lt;step&gt;/&lt;name&gt;/[&lt;target&gt;/]&lt;record&gt;.md
///     runs/&lt;run-id&gt;.md
///     state/project-state.md
///     notes/                       (operator's own notes; never read, never validated)
/// </summary>
public sealed class ProjectPaths
{
    /// <summary>
    /// Feature 1.12 - the fixed, code-defined allowlist of directories the workflow does not own.
    /// Static and checked in; there is no per-run or config-driven exception mechanism, so
    /// changing this is a code change like any other.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedForeignDirectories =
        [".git", ".hg", ".svn", ".vs", ".vscode", ".idea"];

    /// <summary>
    /// Feature 1.12 - the sanctioned operator-note convention. Anything under <c>notes/</c>, and
    /// any file named <c>*.note.md</c>, is ignored by the workflow entirely: never validated,
    /// never hashed, never read as input.
    /// </summary>
    public const string NotesDirectoryName = "notes";

    public const string NoteFileSuffix = ".note.md";

    /// <summary>Directories the workflow itself owns and writes into.</summary>
    public static readonly IReadOnlyList<string> OwnedDirectories = ["artifacts", "records", "runs", "state"];

    private static readonly Regex InvalidTargetChars = new(@"[^A-Za-z0-9._\-]", RegexOptions.Compiled);

    public ProjectPaths(string projectRoot)
    {
        Root = Path.GetFullPath(projectRoot);
        Artifacts = Path.Combine(Root, "artifacts");
        Records = Path.Combine(Root, "records");
        Runs = Path.Combine(Root, "runs");
        State = Path.Combine(Root, "state");
        Notes = Path.Combine(Root, NotesDirectoryName);
    }

    public string Root { get; }

    public string Artifacts { get; }

    public string Records { get; }

    public string Runs { get; }

    public string State { get; }

    public string Notes { get; }

    public string StateFile => Path.Combine(State, "project-state.md");

    public string RunLogFile(string runId) => Path.Combine(Runs, $"{runId}.md");

    /// <summary>
    /// Record directory for a step, optionally narrowed to one iteration target. Step ids are
    /// slash-separated by construction (feature 1.3), so the id maps directly onto directories.
    /// </summary>
    public string RecordDirectory(string stepId, string? iterationTarget)
    {
        var directory = Path.Combine(Records, stepId.Replace('/', Path.DirectorySeparatorChar));
        return iterationTarget is null ? directory : Path.Combine(directory, SanitizeTarget(iterationTarget));
    }

    public string ArtifactDirectory(string artifactId, string? iterationTarget)
    {
        var directory = Path.Combine(Artifacts, artifactId);
        return iterationTarget is null ? directory : Path.Combine(directory, SanitizeTarget(iterationTarget));
    }

    public string ArtifactFile(string artifactId, string? iterationTarget, int version)
        => Path.Combine(ArtifactDirectory(artifactId, iterationTarget), $"v{version}.md");

    /// <summary>A path relative to the project root, with forward slashes, for recording.</summary>
    public string Relative(string absolutePath)
        => Path.GetRelativePath(Root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    public string Absolute(string relativePath)
        => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Section 10, decision 2 - the per-target directory is named by the target's own stable
    /// identifier, sanitized for Windows path safety. When sanitization actually changed
    /// anything, a short digest of the original is appended so two distinct targets can never
    /// collapse onto one directory. Ordinal position is never a directory name; it lives as data
    /// inside the frozen iteration list (5.1) and inside each item's record.
    /// </summary>
    public static string SanitizeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new HaltException("An iteration target has an empty identity; targets must be identifiable to be recordable.");
        }

        var sanitized = InvalidTargetChars.Replace(target, "_").Trim('.', ' ');
        if (sanitized.Length == 0)
        {
            sanitized = "target";
        }

        if (sanitized.Length > 60)
        {
            sanitized = sanitized[..60];
        }

        if (sanitized == target)
        {
            return sanitized;
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target)))[..8];
        return $"{sanitized}-{digest}";
    }
}
