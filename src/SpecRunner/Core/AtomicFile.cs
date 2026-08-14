using System.Text;

namespace SpecRunner.Core;

/// <summary>
/// Feature 2.4 - write-temp-then-rename for every file the application produces.
///
/// No record ever names a half-written file, including under disk-full or hard-kill. This is
/// the mechanical guarantee behind the artifact-before-record ordering, and it is what makes
/// feature 9.4's claim - that killing the process at any instant is recoverable - true rather
/// than hopeful.
///
/// There is no delete path in this class, and none anywhere else in the application (1.7).
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> via a sibling temp file and
    /// a rename. Fails loudly if the destination already exists - every caller in this
    /// application either writes a new versioned file or has already decided that an existing
    /// file is a condition to report, never something to clobber (Pillar 3, Pillar 7).
    /// </summary>
    public static void WriteNew(string path, string content)
    {
        if (File.Exists(path))
        {
            throw new HaltException(
                $"Refusing to overwrite an existing file: {path}. " +
                "Nothing in this application overwrites a file it has already written; " +
                "this is a defect in the caller, not a condition to recover from.");
        }

        WriteInternal(path, content);
    }

    /// <summary>
    /// Writes <paramref name="path"/> atomically, replacing it if present. Used only for the
    /// project state file, which feature 1.13 defines as a rebuilt projection rather than a
    /// record - it is regenerated from scratch on every startup and is never an input.
    /// </summary>
    public static void WriteProjection(string path, string content)
    {
        WriteInternal(path, content);
    }

    private static void WriteInternal(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new HaltException($"Path has no directory component: {path}");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
