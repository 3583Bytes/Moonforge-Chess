using ChessBin.Online;
using Match = ChessBin.Online.Match;

namespace ChessBin.Web.Tests;

/// <summary>
/// Pairing strangers. Ids come from a counter and the time is passed in, so every one of
/// these runs the same way twice — which is the point of the match core taking both.
/// </summary>
[TestFixture]
public class LobbyTests
{
    private const long T0 = 1_000_000;

    private int _next;
    private Lobby _lobby = null!;

    [SetUp]
    public void SetUp()
    {
        _next = 0;
        _lobby = new Lobby(() => $"m{++_next}");
    }

    [Test]
    public void The_first_player_waits()
    {
        SeekResult result = _lobby.Seek("ann", MatchClock.Blitz, T0);

        Assert.That(result.Queued, Is.True);
        Assert.That(result.Paired, Is.False);
        Assert.That(_lobby.WaitingCount, Is.EqualTo(1));
    }

    [Test]
    public void The_second_player_gets_a_game()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);
        SeekResult result = _lobby.Seek("bob", MatchClock.Blitz, T0 + 5_000);

        Assert.That(result.Paired, Is.True);
        Assert.That(result.MatchId, Is.EqualTo("m1"));
        Assert.That(result.Seat, Is.EqualTo(Seat.Black));
        Assert.That(_lobby.WaitingCount, Is.Zero);

        Match match = _lobby.Get(result.MatchId)!;
        Assert.That(match.Status, Is.EqualTo(MatchStatus.Playing));
        Assert.That(match.SeatOf("ann"), Is.EqualTo(Seat.White));
        Assert.That(match.SeatOf("bob"), Is.EqualTo(Seat.Black));
    }

    [Test]
    public void Different_time_controls_do_not_pair()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);
        SeekResult result = _lobby.Seek("bob", MatchClock.Bullet, T0);

        Assert.That(result.Queued, Is.True);
        Assert.That(_lobby.WaitingCount, Is.EqualTo(2));
        Assert.That(_lobby.MatchCount, Is.Zero);
    }

    [Test]
    public void Identical_time_controls_pair_even_when_they_are_separate_objects()
    {
        _lobby.Seek("ann", new MatchClock(180_000, 2_000), T0);
        SeekResult result = _lobby.Seek("bob", new MatchClock(180_000, 2_000), T0);

        Assert.That(result.Paired, Is.True);
    }

    [Test]
    public void A_player_cannot_be_paired_with_themselves()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);
        SeekResult again = _lobby.Seek("ann", MatchClock.Blitz, T0 + 1_000);

        Assert.That(again.Queued, Is.True);
        Assert.That(_lobby.WaitingCount, Is.EqualTo(1), "the second request replaces the first");
        Assert.That(_lobby.MatchCount, Is.Zero);
    }

    [Test]
    public void Whoever_waited_longest_is_paired_first()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);
        _lobby.Seek("bob", MatchClock.Blitz, T0 + 1_000);      // pairs with ann
        _lobby.Seek("cat", MatchClock.Blitz, T0 + 2_000);
        _lobby.Seek("dan", MatchClock.Blitz, T0 + 3_000);      // pairs with cat

        Assert.That(_lobby.Get("m1")!.SeatOf("ann"), Is.EqualTo(Seat.White));
        Assert.That(_lobby.Get("m1")!.SeatOf("bob"), Is.EqualTo(Seat.Black));
        Assert.That(_lobby.Get("m2")!.SeatOf("cat"), Is.EqualTo(Seat.White));
        Assert.That(_lobby.Get("m2")!.SeatOf("dan"), Is.EqualTo(Seat.Black));
    }

    [Test]
    public void Every_game_gets_its_own_id()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);
        string first = _lobby.Seek("bob", MatchClock.Blitz, T0)!.MatchId!;
        _lobby.Seek("cat", MatchClock.Blitz, T0);
        string second = _lobby.Seek("dan", MatchClock.Blitz, T0)!.MatchId!;

        Assert.That(second, Is.Not.EqualTo(first));
        Assert.That(_lobby.MatchCount, Is.EqualTo(2));
    }

    [Test]
    public void A_request_can_be_withdrawn()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);

        Assert.That(_lobby.Cancel("ann"), Is.True);
        Assert.That(_lobby.Cancel("ann"), Is.False);
        Assert.That(_lobby.Seek("bob", MatchClock.Blitz, T0).Queued, Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void A_nameless_player_is_refused(string token)
    {
        Assert.That(_lobby.Seek(token, MatchClock.Blitz, T0).Queued, Is.False);
        Assert.That(_lobby.WaitingCount, Is.Zero);
    }

    [Test]
    public void An_unknown_match_id_returns_nothing()
    {
        Assert.That(_lobby.Get("m404"), Is.Null);
        Assert.That(_lobby.Get(null), Is.Null);
    }

    // ── the housekeeping a host runs on a timer ─────────────────────────────

    [Test]
    public void A_request_nobody_answered_goes_stale()
    {
        _lobby.Seek("ann", MatchClock.Blitz, T0);

        _lobby.Sweep(T0 + 60_000, seekTimeoutMs: 120_000, finishedRetentionMs: 60_000);
        Assert.That(_lobby.WaitingCount, Is.EqualTo(1), "still inside the window");

        _lobby.Sweep(T0 + 120_000, seekTimeoutMs: 120_000, finishedRetentionMs: 60_000);
        Assert.That(_lobby.WaitingCount, Is.Zero);
    }

    [Test]
    public void Sweeping_makes_flags_fall_in_games_nobody_is_watching()
    {
        _lobby.Seek("ann", new MatchClock(60_000, 0), T0);
        string id = _lobby.Seek("bob", new MatchClock(60_000, 0), T0)!.MatchId!;

        IReadOnlyList<string> ended = _lobby.Sweep(T0 + 61_000, 120_000, 600_000);

        Assert.That(ended, Is.EqualTo(new[] { id }));
        Assert.That(_lobby.Get(id)!.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
        Assert.That(_lobby.Get(id)!.Reason, Is.EqualTo(MatchReason.Timeout));
    }

    [Test]
    public void A_game_only_ends_once_however_often_it_is_swept()
    {
        _lobby.Seek("ann", new MatchClock(60_000, 0), T0);
        _lobby.Seek("bob", new MatchClock(60_000, 0), T0);

        Assert.That(_lobby.Sweep(T0 + 61_000, 120_000, 600_000), Has.Count.EqualTo(1));
        Assert.That(_lobby.Sweep(T0 + 62_000, 120_000, 600_000), Is.Empty);
    }

    [Test]
    public void Finished_games_are_kept_a_while_then_cleared()
    {
        _lobby.Seek("ann", MatchClock.Untimed, T0);
        string id = _lobby.Seek("bob", MatchClock.Untimed, T0)!.MatchId!;
        _lobby.Get(id)!.Resign("ann", T0 + 30_000);

        _lobby.Sweep(T0 + 60_000, 120_000, finishedRetentionMs: 600_000);
        Assert.That(_lobby.Get(id), Is.Not.Null, "a player still needs to read the result");

        _lobby.Sweep(T0 + 700_000, 120_000, finishedRetentionMs: 600_000);
        Assert.That(_lobby.Get(id), Is.Null);
        Assert.That(_lobby.MatchCount, Is.Zero);
    }

    [Test]
    public void A_game_in_progress_is_never_cleared_away()
    {
        _lobby.Seek("ann", MatchClock.Untimed, T0);
        string id = _lobby.Seek("bob", MatchClock.Untimed, T0)!.MatchId!;

        _lobby.Sweep(T0 + 100_000_000, 120_000, 600_000);

        Assert.That(_lobby.Get(id), Is.Not.Null);
        Assert.That(_lobby.Get(id)!.Status, Is.EqualTo(MatchStatus.Playing));
    }
}
