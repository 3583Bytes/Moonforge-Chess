namespace ChessBin.Online;

/// <summary>What came of asking for a game.</summary>
/// <param name="MatchId">Set once an opponent was found.</param>
/// <param name="Match">The game that was created, if one was.</param>
/// <param name="Seat">Which side the asking player got.</param>
/// <param name="Queued">True when nobody was waiting and the request is now the one waiting.</param>
public sealed record SeekResult(string? MatchId, Match? Match, Seat? Seat, bool Queued)
{
    public bool Paired => Match is not null;
    public static readonly SeekResult Refused = new(null, null, null, false);
}

/// <summary>
/// Pairs strangers who want the same time control, and holds the games it makes.
/// <para>
/// Like <see cref="Match"/>, it has no clock and no I/O of its own: the current time is
/// passed in and new match ids come from an injected factory, so the whole of pairing —
/// including fairness and expiry — is testable without a host. A real deployment will run
/// one of these per process behind whatever transport is chosen; nothing here assumes which.
/// </para>
/// </summary>
public sealed class Lobby
{
    private sealed record Waiting(string Token, MatchClock Clock, long SinceMs);

    private readonly Func<string> _newMatchId;
    private readonly Dictionary<string, Match> _matches = [];
    private readonly List<Waiting> _waiting = [];

    /// <param name="newMatchId">
    /// Supplies ids for new matches — <c>Guid.NewGuid</c> in production, a counter in tests.
    /// Injected for the same reason the time is: so a run is reproducible.
    /// </param>
    public Lobby(Func<string> newMatchId)
    {
        ArgumentNullException.ThrowIfNull(newMatchId);
        _newMatchId = newMatchId;
    }

    public int WaitingCount => _waiting.Count;
    public int MatchCount => _matches.Count;

    public Match? Get(string? matchId) =>
        matchId is not null && _matches.TryGetValue(matchId, out Match? match) ? match : null;

    /// <summary>
    /// Asks for a game. Pairs with whoever has been waiting longest on the same time
    /// control, and otherwise joins the queue. Asking twice replaces the earlier request
    /// rather than pairing a player with themselves.
    /// </summary>
    public SeekResult Seek(string token, MatchClock clock, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(token)) return SeekResult.Refused;
        ArgumentNullException.ThrowIfNull(clock);

        _waiting.RemoveAll(w => w.Token == token);

        // Longest wait first, which is the only fair order and the one players notice.
        int index = _waiting.FindIndex(w => w.Clock == clock);
        if (index < 0)
        {
            _waiting.Add(new Waiting(token, clock, nowMs));
            return new SeekResult(null, null, null, Queued: true);
        }

        Waiting opponent = _waiting[index];
        _waiting.RemoveAt(index);

        // The player who waited gets White. Arbitrary, but fixed and explainable, which
        // beats a coin flip nobody can reproduce when a game is disputed.
        var match = new Match(clock, nowMs);
        match.Join(opponent.Token, nowMs);
        Seat? seat = match.Join(token, nowMs);

        string id = _newMatchId();
        _matches[id] = match;
        return new SeekResult(id, match, seat, Queued: false);
    }

    /// <summary>Withdraws a request that has not been paired yet.</summary>
    public bool Cancel(string? token) => token is not null && _waiting.RemoveAll(w => w.Token == token) > 0;

    /// <summary>
    /// The housekeeping a host has to run on a timer: flags fall, requests from players who
    /// closed the tab go stale, and finished games stop being worth keeping. None of it
    /// happens on its own, because nothing here watches a clock.
    /// </summary>
    /// <returns>The matches that ended during this sweep.</returns>
    public IReadOnlyList<string> Sweep(long nowMs, long seekTimeoutMs, long finishedRetentionMs)
    {
        _waiting.RemoveAll(w => nowMs - w.SinceMs >= seekTimeoutMs);

        List<string> ended = [];
        foreach ((string id, Match match) in _matches)
        {
            if (match.ExpireIfFlagged(nowMs)) ended.Add(id);
        }

        foreach (string id in _matches
                     .Where(pair => pair.Value.FinishedAtMs is long done && nowMs - done >= finishedRetentionMs)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _matches.Remove(id);
        }

        return ended;
    }
}
