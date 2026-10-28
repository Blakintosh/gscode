namespace GSCode.Workspace.Database;

/// <summary>
/// Counts the record writes a database has taken, and remembers which file the latest unbroken
/// run of them came from — so an answer computed from the whole database can tell, later, whether
/// anything but one file has been written since.
///
/// That is the question a cached answer about an OPEN file has to ask. Typing commits the open
/// file on every analysis, and nothing else; an answer that would only change with another file's
/// writes stays good across all of them, and goes the moment any other file is written. Every
/// script and header write ticks it, from the stores and from the header store alike, so no write
/// can change the database without being counted.
/// </summary>
public sealed class CommitClock
{
    private sealed record State(long Tick, string? StreakPath, long StreakStart);

    private State _state = new(0, null, 0);

    /// <summary>The number of writes so far.</summary>
    public long Now
    {
        get { return Volatile.Read(ref _state).Tick; }
    }

    /// <summary>Counts one write of <paramref name="path"/>.</summary>
    public void Advance(string path)
    {
        while ( true )
        {
            State current = Volatile.Read(ref _state);
            State next = string.Equals(current.StreakPath, path, StringComparison.Ordinal)
                ? current with { Tick = current.Tick + 1 }
                : new State(current.Tick + 1, path, current.Tick);

            if ( ReferenceEquals(Interlocked.CompareExchange(ref _state, next, current), current) )
            {
                return;
            }
        }
    }

    /// <summary>
    /// Whether every write since <paramref name="tick"/> — none at all included — was a write of
    /// <paramref name="path"/>.
    /// </summary>
    public bool OnlyWrittenSince(long tick, string path)
    {
        State current = Volatile.Read(ref _state);
        if ( current.Tick == tick )
        {
            return true;
        }

        return string.Equals(current.StreakPath, path, StringComparison.Ordinal) && current.StreakStart <= tick;
    }
}
