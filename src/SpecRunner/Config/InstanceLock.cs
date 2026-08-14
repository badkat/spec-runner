using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SpecRunner.Core;

namespace SpecRunner.Config;

/// <summary>
/// Feature 9.1 - a single-instance lock file carrying the PID.
///
/// A second instance halts with the PID of the holder. A stale lock whose PID is not alive is
/// reported and requires explicit operator clearance, because auto-clearing is guessing: the
/// application cannot tell "the last run was killed" from "another run is starting right now",
/// and Pillar 8 says it must not pick the more likely of the two.
///
/// Two deliberate choices here:
///   - The lock lives outside the project directory, because feature 1.12 requires everything
///     inside the project tree to be Markdown the workflow produced.
///   - Releasing the lock rewrites its state rather than deleting the file, so feature 1.7's
///     "there is no code path anywhere in the application that deletes a file" stays literally
///     true. The lock is a state, and a released lock is a state worth being able to read.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private const string Held = "held";
    private const string Released = "released";

    private readonly string _path;
    private readonly string _runId;
    private readonly string _projectDirectory;

    private InstanceLock(string path, string runId, string projectDirectory)
    {
        _path = path;
        _runId = runId;
        _projectDirectory = projectDirectory;
    }

    public static string PathFor(string projectDirectory)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpecRunner",
            "locks");
        Directory.CreateDirectory(root);

        var key = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectDirectory).ToLowerInvariant())))[..16];

        return Path.Combine(root, $"{key}.lock.md");
    }

    public static InstanceLock Acquire(string projectDirectory, string runId, bool operatorClearedStaleLock)
    {
        var path = PathFor(projectDirectory);
        var full = Path.GetFullPath(projectDirectory);

        if (File.Exists(path))
        {
            var doc = MdDoc.Parse(File.ReadAllText(path), path);
            var state = doc.Require("lock_state", path);
            var pidText = doc.Require("pid", path);

            if (state == Held)
            {
                var alive = int.TryParse(pidText, out var pid) && IsAlive(pid);

                if (alive)
                {
                    throw new HaltException(
                        "Another instance is already running against this project directory.\n" +
                        $"  Holder PID: {pidText}\n" +
                        $"  Held since: {doc.Require("acquired_utc", path)}\n" +
                        $"  Run id:     {doc.Require("run_id", path)}\n" +
                        $"  Lock file:  {path}\n" +
                        $"  Project:    {full}\n" +
                        "This application runs one project at a time (Pillar 5). Stop the other instance first.");
                }

                if (!operatorClearedStaleLock)
                {
                    throw new HaltException(
                        "A lock is held but the process that took it is not alive.\n" +
                        $"  Recorded PID: {pidText}\n" +
                        $"  Held since:   {doc.Require("acquired_utc", path)}\n" +
                        $"  Run id:       {doc.Require("run_id", path)}\n" +
                        $"  Lock file:    {path}\n" +
                        "Clearing it automatically would be a guess: this application cannot distinguish a run that " +
                        "was killed from one that is starting right now. Re-run with --clear-stale-lock to say which.");
                }
            }
        }

        Write(path, Held, runId, full, "Held by a running instance.");
        return new InstanceLock(path, runId, full);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Write(string path, string state, string runId, string projectDirectory, string note)
    {
        var doc = new MdDoc()
            .Set("record_kind", "instance-lock")
            .Set("lock_state", state)
            .Set("pid", Environment.ProcessId)
            .Set("run_id", runId)
            .Set("acquired_utc", RunIdentity.TimestampUtc())
            .Set("project_directory", projectDirectory);
        doc.Body = note;

        AtomicFile.WriteProjection(path, doc.Serialize());
    }

    public void Dispose() => Write(_path, Released, _runId, _projectDirectory, "Released cleanly by the holding process.");
}
