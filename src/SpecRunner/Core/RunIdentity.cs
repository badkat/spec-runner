using System.Globalization;
using System.Security.Cryptography;

namespace SpecRunner.Core;

/// <summary>
/// Feature 2.7 / 9.6 - run identity and in-run sequence numbers.
///
/// Every file the application writes names the run that wrote it and a monotonically increasing
/// sequence number within that run. That is what makes "what did the interrupted run produce?"
/// a question a person answers by reading, rather than one they infer from filesystem metadata
/// (which feature 1.4 forbids consulting at all).
///
/// The run id leads with a UTC timestamp so that lexicographic order over run ids is
/// chronological order over runs - ordering is readable from the records themselves.
/// </summary>
public sealed class RunIdentity
{
    private int _sequence;

    private RunIdentity(string id, DateTime startedUtc)
    {
        Id = id;
        StartedUtc = startedUtc;
    }

    public string Id { get; }

    public DateTime StartedUtc { get; }

    public static RunIdentity New()
    {
        var now = DateTime.UtcNow;
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        var id = $"run-{now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{suffix}";
        return new RunIdentity(id, now);
    }

    /// <summary>Next sequence number in this run. The first number handed out is 1.</summary>
    public int NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Highest sequence number handed out so far, for reporting.</summary>
    public int CurrentSequence => Volatile.Read(ref _sequence);

    /// <summary>Feature 9.6 - UTC ISO-8601, everywhere, with no local-time variant anywhere.</summary>
    public static string TimestampUtc()
        => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
