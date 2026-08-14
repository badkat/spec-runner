namespace SpecRunner.Surfaces;

/// <summary>
/// The terminal surface. This is one of exactly two files permitted to touch System.Console; the
/// build fails if any other source file does (feature 8.1, enforced in SpecRunner.csproj).
///
/// Feature 8.9 scopes what may arrive here: startup self-checks, config resolution, port
/// binding, unhandled exceptions with full stack traces, and shutdown reason. Nothing about
/// workflow state, ever.
/// </summary>
internal static class TerminalSurface
{
    private static readonly object Gate = new();

    static TerminalSurface()
    {
        // Both surfaces and every file this application writes are UTF-8. A legacy console
        // codepage would silently turn characters into question marks, and a diagnostic the
        // operator cannot read is a diagnostic that did not arrive (Pillar 2).
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached (output redirected to a file or a pipe). The bytes written are
            // UTF-8 either way; only the console's own decoding was unavailable to set.
        }
    }

    public static void Write(EmittedEvent e)
    {
        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = e.Kind switch
            {
                EventKinds.Fatal => ConsoleColor.Red,
                EventKinds.StartupBlock => ConsoleColor.Yellow,
                EventKinds.Shutdown => ConsoleColor.DarkYellow,
                _ => ConsoleColor.DarkGray
            };
            Console.Write($"[{e.TimestampUtc}] {e.Kind,-17} ");
            Console.ForegroundColor = previous;

            var lines = e.Message.Split('\n');
            Console.WriteLine(lines[0]);
            for (var i = 1; i < lines.Length; i++)
            {
                Console.WriteLine($"{new string(' ', 32)}{lines[i]}");
            }

            foreach (var field in e.Fields)
            {
                Console.WriteLine($"{new string(' ', 32)}{field.Key}: {field.Value}");
            }

            Console.Out.Flush();
        }
    }
}
