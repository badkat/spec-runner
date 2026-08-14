using SpecRunner.Core;

namespace SpecRunner.Surfaces;

/// <summary>
/// Feature 8.1 - the single emit API with a mandatory surface parameter.
///
/// Every call site in the application faces a forced choice between the two surfaces Pillar 2
/// names. There is no default surface, no "info" convenience overload that picks one, and no
/// bypass: the build fails if any file outside TerminalSurface.cs and SurfaceLoggerProvider.cs
/// reaches for System.Console or an ambient logging framework.
/// </summary>
public static class Emit
{
    private static readonly object Gate = new();
    private static readonly List<EmittedEvent> Pending = [];
    private static RunIdentity? _run;
    private static ConsoleBroker? _broker;
    private static RunLog? _runLog;

    /// <summary>Called once, before anything else, so every event carries run id and sequence.</summary>
    public static void Initialize(RunIdentity run, ConsoleBroker broker)
    {
        lock (Gate)
        {
            _run = run;
            _broker = broker;
        }
    }

    /// <summary>
    /// Attaches the on-disk run log. Events emitted before the project root was known (config
    /// resolution, and any failure during it) are flushed into it in order, so the log is a
    /// complete account of the run rather than an account of the part after setup succeeded.
    /// </summary>
    public static void AttachRunLog(RunLog runLog)
    {
        lock (Gate)
        {
            _runLog = runLog;
            foreach (var pending in Pending)
            {
                runLog.Append(pending);
            }

            Pending.Clear();
        }
    }

    public static void To(
        Surface surface,
        string kind,
        string message,
        IReadOnlyList<KeyValuePair<string, string>>? fields = null,
        object? data = null,
        bool transientDisplayOnly = false)
    {
        var e = new EmittedEvent(
            _run?.NextSequence() ?? 0,
            RunIdentity.TimestampUtc(),
            surface,
            kind,
            message,
            fields ?? [])
        {
            Data = data,
            TransientDisplayOnly = transientDisplayOnly
        };

        if (surface == Surface.Terminal)
        {
            TerminalSurface.Write(e);
        }
        else
        {
            _broker?.Publish(e);
        }

        lock (Gate)
        {
            if (_runLog is null)
            {
                Pending.Add(e);
            }
            else
            {
                _runLog.Append(e);
            }
        }
    }

    /// <summary>Convenience for building the field list at a call site without ceremony.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(params string[] keysAndValues)
    {
        if (keysAndValues.Length % 2 != 0)
        {
            throw new HaltException("Emit.Fields requires an even number of arguments (key, value, key, value...).");
        }

        var fields = new List<KeyValuePair<string, string>>(keysAndValues.Length / 2);
        for (var i = 0; i < keysAndValues.Length; i += 2)
        {
            fields.Add(new KeyValuePair<string, string>(keysAndValues[i], keysAndValues[i + 1]));
        }

        return fields;
    }

    public static void Flush()
    {
        lock (Gate)
        {
            _runLog?.Dispose();
            _runLog = null;
        }
    }
}
