using ChessBin.Online;
using ChessBin.Web;

namespace ChessBin.Web.Tests;

/// <summary>
/// The browser half of online play, against a scripted server.
/// <para>
/// These are the tests that matter most in this design, because the server deliberately knows
/// no chess: everything stopping an opponent from playing an illegal move, or claiming a win
/// they did not get, is in <see cref="OnlineSession"/>. If these pass, a chess-ignorant server
/// is safe; if they do not, it is not.
/// </para>
/// </summary>
[TestFixture]
public class OnlineSessionTests
{
    private const string Token = "player-token-01";
    private const string MatchId = "11111111-2222-3333-4444-555555555555";

    /// <summary>A server that answers with whatever the test last told it to, and remembers calls.</summary>
    private sealed class FakeApi : IPlayApi
    {
        public SeekOutcome NextSeek = SeekOutcome.Unreachable;
        public MatchView? NextView;

        public int Disputes { get; private set; }
        public int? DisputedPly { get; private set; }
        public List<(string San, string Uci, MatchOutcome? Outcome, MatchReason? Reason)> Sent { get; } = [];
        public List<string> Actions { get; } = [];

        public Task<SeekOutcome> SeekAsync(string token, MatchClock clock, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextSeek);

        public Task CancelSeekAsync(string token, CancellationToken cancellationToken = default)
        {
            Actions.Add("cancel");
            return Task.CompletedTask;
        }

        public Task<MatchView?> JoinAsync(string matchId, string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextView);

        public Task<MatchView?> StateAsync(string matchId, string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextView);

        public Task<MatchView?> MoveAsync(string matchId, string token, string san, string uci,
                                          MatchOutcome? outcome, MatchReason? reason,
                                          CancellationToken cancellationToken = default)
        {
            Sent.Add((san, uci, outcome, reason));
            return Task.FromResult(NextView);
        }

        public Task<MatchView?> ActAsync(string matchId, string token, string action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.FromResult(NextView);
        }

        public Task<MatchView?> DisputeAsync(string matchId, string token, int ply, CancellationToken cancellationToken = default)
        {
            Disputes++;
            DisputedPly = ply;
            return Task.FromResult(NextView);
        }
    }

    private static MatchView View(
        string[] uci,
        Seat seat = Seat.White,
        MatchStatus status = MatchStatus.Playing,
        MatchOutcome outcome = MatchOutcome.None,
        MatchReason reason = MatchReason.None,
        bool opponentJoined = true,
        bool drawOfferedToYou = false) =>
        new(status, outcome, reason, seat,
            Moves: uci, Uci: uci,
            ToMove: uci.Length % 2 == 0 ? Seat.White : Seat.Black,
            WhiteMs: 180_000, BlackMs: 180_000,
            DrawOfferedBy: drawOfferedToYou ? (seat == Seat.White ? Seat.Black : Seat.White) : null,
            DrawOfferedToYou: drawOfferedToYou,
            OpponentJoined: opponentJoined);

    /// <summary>Gets a session all the way to a live game, which is the start of most tests.</summary>
    private static async Task<(OnlineSession Session, FakeApi Api)> PlayingAsync(Seat seat = Seat.White)
    {
        var api = new FakeApi
        {
            NextSeek = new SeekOutcome(Paired: true, Queued: false, MatchId, seat, 0),
            NextView = View([], seat),
        };

        var session = new OnlineSession(api, Token);
        await session.SeekAsync(MatchClock.Blitz);
        return (session, api);
    }

    [Test]
    public async Task WaitingInTheQueueIsReportedRatherThanLookingLikeAFailure()
    {
        var api = new FakeApi { NextSeek = new SeekOutcome(false, Queued: true, null, null, 3) };
        var session = new OnlineSession(api, Token);

        await session.SeekAsync(MatchClock.Blitz);

        Assert.Multiple(() =>
        {
            Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Seeking));
            Assert.That(session.Status, Does.Contain("3"));
        });
    }

    [Test]
    public async Task BeingPairedSeatsThePlayerAndStartsTheGame()
    {
        (OnlineSession session, _) = await PlayingAsync(Seat.Black);

        Assert.Multiple(() =>
        {
            Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Playing));
            Assert.That(session.Seat, Is.EqualTo(Seat.Black));
            Assert.That(session.MatchId, Is.EqualTo(MatchId));
            Assert.That(session.WhiteAtBottom, Is.False, "playing Black means looking from Black's side");
        });
    }

    [Test]
    public async Task AGameWhoseOpponentHasNotArrivedIsNotYetPlayable()
    {
        var api = new FakeApi
        {
            NextSeek = new SeekOutcome(true, false, MatchId, Seat.White, 0),
            NextView = View([], Seat.White, opponentJoined: false),
        };
        var session = new OnlineSession(api, Token);

        await session.SeekAsync(MatchClock.Blitz);

        Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Seated));
        Assert.That(session.IsMyTurn, Is.False);
    }

    [Test]
    public async Task TheOpponentsLegalMoveIsPlayedOntoTheBoard()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.Black);

        api.NextView = View(["e2e4"], Seat.Black);
        await session.PollAsync();

        Assert.Multiple(() =>
        {
            Assert.That(session.Moves, Has.Count.EqualTo(1));
            Assert.That(session.Moves[0], Is.EqualTo("e4"), "the SAN comes from this engine, not from the wire");
            Assert.That(session.Disputed, Is.False);
            Assert.That(session.IsMyTurn, Is.True);
        });
    }

    /// <summary>
    /// The core safety property. The server relayed a move it cannot evaluate; this engine
    /// refuses it, so it never reaches the board, and the game is stopped for both players.
    /// </summary>
    [Test]
    public async Task AnIllegalMoveFromTheOpponentIsRefusedAndDisputed()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.Black);

        // A rook cannot leap its own pawn on move one.
        api.NextView = View(["a1a5"], Seat.Black);
        await session.PollAsync();

        Assert.Multiple(() =>
        {
            Assert.That(session.Disputed, Is.True);
            Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Finished));
            Assert.That(session.Moves, Is.Empty, "an illegal move must never reach the board");
            Assert.That(api.Disputes, Is.EqualTo(1));
            Assert.That(api.DisputedPly, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The other half of it: a legal move carrying a false result. The server has no way to
    /// know, so believing it here would hand the cheat the game.
    /// </summary>
    [Test]
    public async Task AClaimedCheckmateThatIsNotOneIsRefusedAndDisputed()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.Black);

        api.NextView = View(["e2e4"], Seat.Black,
            status: MatchStatus.Finished, outcome: MatchOutcome.WhiteWins, reason: MatchReason.Checkmate);
        await session.PollAsync();

        Assert.Multiple(() =>
        {
            Assert.That(session.Disputed, Is.True);
            Assert.That(api.Disputes, Is.EqualTo(1));
            Assert.That(session.Status, Does.Contain("does not support"));
        });
    }

    [Test]
    public async Task ARealCheckmateIsAccepted()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        // Fool's mate: 1. f3 e5 2. g4 Qh4#
        api.NextView = View(["f2f3", "e7e5", "g2g4", "d8h4"], Seat.White,
            status: MatchStatus.Finished, outcome: MatchOutcome.BlackWins, reason: MatchReason.Checkmate);
        await session.PollAsync();

        Assert.Multiple(() =>
        {
            Assert.That(session.Disputed, Is.False, "a genuine mate must not be treated as a cheat");
            Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Finished));
            Assert.That(session.Moves, Has.Count.EqualTo(4));
            Assert.That(session.Status, Does.Contain("lost"), "White lost this one");
        });
    }

    /// <summary>
    /// Resignation, agreement and timeouts are the server's to decide — there is nothing on the
    /// board that would confirm them, so they must not be checked against the engine.
    /// </summary>
    [Test]
    public async Task AResignationIsBelievedWithoutTheBoardAgreeing()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        api.NextView = View(["e2e4"], Seat.White,
            status: MatchStatus.Finished, outcome: MatchOutcome.WhiteWins, reason: MatchReason.Resignation);
        await session.PollAsync();

        Assert.Multiple(() =>
        {
            Assert.That(session.Disputed, Is.False);
            Assert.That(session.Status, Does.Contain("won"));
        });
    }

    [Test]
    public async Task ATimeoutIsBelievedWithoutTheBoardAgreeing()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        api.NextView = View([], Seat.White,
            status: MatchStatus.Finished, outcome: MatchOutcome.BlackWins, reason: MatchReason.Timeout);
        await session.PollAsync();

        Assert.That(session.Disputed, Is.False);
        Assert.That(session.Status, Does.Contain("on time"));
    }

    [Test]
    public async Task PlayingAMoveSendsTheEnginesNotationAndCoordinates()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        // e2 is column 4, row 6; e4 is column 4, row 4. Index 0 is a8 throughout.
        await session.ClickSquareAsync(4, 6);
        await session.ClickSquareAsync(4, 4);

        Assert.That(api.Sent, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(api.Sent[0].San, Is.EqualTo("e4"));
            Assert.That(api.Sent[0].Uci, Is.EqualTo("e2e4"));
            Assert.That(api.Sent[0].Outcome, Is.Null, "a normal move claims nothing");
        });
    }

    [Test]
    public async Task AMoveThisEngineRejectsIsNeverSent()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        // a1 to a5: the rook is blocked by its own pawn, so this is not a legal target and the
        // click should not even be treated as a move.
        await session.ClickSquareAsync(0, 7);
        await session.ClickSquareAsync(0, 3);

        Assert.That(api.Sent, Is.Empty);
    }

    [Test]
    public async Task PlayingOnAnOpponentsTurnIsIgnored()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.Black);

        await session.ClickSquareAsync(4, 1);
        await session.ClickSquareAsync(4, 3);

        Assert.That(api.Sent, Is.Empty, "it is White's move");
    }

    [Test]
    public async Task DeliveringMateSendsTheClaimAlongWithTheMove()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.Black);

        // Walk White into fool's mate, then deliver it as Black.
        api.NextView = View(["f2f3"], Seat.Black);
        await session.PollAsync();

        await session.ClickSquareAsync(4, 1);   // e7
        await session.ClickSquareAsync(4, 3);   // e5

        api.NextView = View(["f2f3", "e7e5", "g2g4"], Seat.Black);
        await session.PollAsync();

        await session.ClickSquareAsync(3, 0);   // d8
        await session.ClickSquareAsync(7, 4);   // h4

        var mate = api.Sent[^1];
        Assert.Multiple(() =>
        {
            Assert.That(mate.San, Is.EqualTo("Qh4#"));
            Assert.That(mate.Outcome, Is.EqualTo(MatchOutcome.BlackWins));
            Assert.That(mate.Reason, Is.EqualTo(MatchReason.Checkmate));
        });
    }

    [Test]
    public async Task ResigningAndOfferingADrawReachTheServer()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        await session.OfferDrawAsync();
        await session.ResignAsync();

        Assert.That(api.Actions, Is.EqualTo(new[] { "draw", "resign" }));
    }

    [Test]
    public async Task ADroppedPollLeavesTheBoardExactlyAsItWas()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.Black);

        api.NextView = View(["e2e4"], Seat.Black);
        await session.PollAsync();

        // The network drops. The next poll has to recover, not tear the game down.
        api.NextView = null;
        await session.PollAsync();

        Assert.Multiple(() =>
        {
            Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Playing));
            Assert.That(session.Moves, Has.Count.EqualTo(1));
            Assert.That(session.Disputed, Is.False);
        });
    }

    [Test]
    public async Task CancellingASearchTellsTheLobbyAndClearsTheState()
    {
        var api = new FakeApi { NextSeek = new SeekOutcome(false, true, null, null, 1) };
        var session = new OnlineSession(api, Token);
        await session.SeekAsync(MatchClock.Blitz);

        await session.CancelSeekAsync();

        Assert.Multiple(() =>
        {
            Assert.That(api.Actions, Does.Contain("cancel"));
            Assert.That(session.Phase, Is.EqualTo(OnlinePhase.Idle));
        });
    }

    [Test]
    public async Task ADrawOfferIsSurfacedToThePlayerBeingOffered()
    {
        (OnlineSession session, FakeApi api) = await PlayingAsync(Seat.White);

        api.NextView = View([], Seat.White, drawOfferedToYou: true);
        await session.PollAsync();

        Assert.That(session.Status, Does.Contain("draw"));
        Assert.That(session.View!.DrawOfferedToYou, Is.True);
    }
}
