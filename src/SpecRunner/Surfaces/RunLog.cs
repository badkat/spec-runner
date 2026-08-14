using System.Text;
using SpecRunner.Core;

namespace SpecRunner.Surfaces;

/// <summary>
/// Feature 8.2 - everything streamed to the browser console is simultaneously appended to a run
/// log on disk, so closing the browser loses nothing. Feature 6.5 names this file, not the
/// terminal, as the recovery path for a block that occurs while nobody has the browser open.
///
/// Terminal events are appended here too, tagged with their surface. The run log is not a third
/// surface - it is the disk record of what the two surfaces said (Pillar 7), and feature 9.5
/// requires the crash detail to land in it before the process exits.
///
/// This is the single documented exception to feature 2.4's write-temp-then-rename rule. 8.2
/// says "appended", and an append is what this does: no record anywhere names the run log by
/// hash, nothing reads it as workflow input, and a torn final line under hard-kill costs one
/// event rather than the whole file. Every other file the application writes goes through
/// AtomicFile.
/// </summary>
public sealed class RunLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public RunLog(ProjectPaths paths, RunIdentity run, string projectRoot, string modelEndpoint)
    {
        Directory.CreateDirectory(paths.Runs);
        Path_ = paths.RunLogFile(run.Id);

        var isNew = !File.Exists(Path_);
        var stream = new FileStream(Path_, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

        if (isNew)
        {
            var header = new MdDoc()
                .Set("record_kind", "run-log")
                .Set("run_id", run.Id)
                .Set("started_utc", RunIdentity.TimestampUtc())
                .Set("project_root", projectRoot)
                .Set("model_endpoint", modelEndpoint)
                .Set("hash_algorithm", Canonical.HashAlgorithm)
                .Set("canonicalization_version", Canonical.Version);
            header.Body = "Chronological log of both surfaces for this run. Terminal entries are tagged\n"
                        + "`terminal`; browser console entries are tagged `console`.";
            _writer.Write(header.Serialize());
            _writer.Flush();
        }
    }

    public string Path_ { get; }

    public void Append(EmittedEvent e)
    {
        if (e.TransientDisplayOnly)
        {
            return;
        }

        var entry = new StringBuilder();
        entry.Append("\n## ")
             .Append(e.Sequence.ToString("D6"))
             .Append(" · ").Append(e.TimestampUtc)
             .Append(" · ").Append(e.Surface == Surface.Terminal ? "terminal" : "console")
             .Append(" · ").Append(e.Kind)
             .Append('\n');

        foreach (var field in e.Fields)
        {
            entry.Append("- ").Append(field.Key).Append(": ").Append(field.Value).Append('\n');
        }

        if (e.Message.Length > 0)
        {
            entry.Append('\n').Append(e.Message).Append('\n');
        }

        lock (_gate)
        {
            _writer.Write(entry.ToString());
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
