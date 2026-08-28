using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

public sealed class VoteChessTests
{
    private const string Start = ChessGameSession.StartingFen;
    private static readonly DateTimeOffset T0 = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The published ballot for the opening position, which is what voters chose from.</summary>
    private static readonly IReadOnlyList<string> Ballot = VoteChess.LegalMoves(Start);

    private static TallyResult Tally(params (string Voter, string San)[] ballots) =>
        VoteChess.Tally(ballots.ToDictionary(b => b.Voter, b => b.San), Ballot);

    [Test]
    public void TheMostVotedMoveWins()
    {
        TallyResult result = Tally(("ann", "e4"), ("bob", "e4"), ("cal", "d4"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Winner, Is.EqualTo("e4"));
            Assert.That(result.Voters, Is.EqualTo(3));
            Assert.That(result.Counts[0].Votes, Is.EqualTo(2));
            Assert.That(result.Counts, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void TiesGoToWhicheverMoveIsFirstOnTheBallot()
    {
        // The ballot is sorted, so this is checkable by anyone who wants to argue about it:
        // "Nf3" precedes "e4" in ordinal order because capitals sort before lowercase.
        TallyResult result = Tally(("ann", "e4"), ("bob", "Nf3"), ("cal", "e4"), ("dee", "Nf3"));

        Assert.That(result.Winner, Is.EqualTo("Nf3"));
        Assert.That(Ballot.ToList().IndexOf("Nf3"), Is.LessThan(Ballot.ToList().IndexOf("e4")));
    }

    [Test]
    public void NobodyVoting_LeavesNoWinnerRatherThanPickingOne()
    {
        TallyResult result = Tally();

        Assert.Multiple(() =>
        {
            Assert.That(result.HasWinner, Is.False);
            Assert.That(result.Winner, Is.Null);
            Assert.That(result.Voters, Is.Zero);
            Assert.That(result.Counts, Is.Empty);
        });
    }

    [Test]
    public void AMoveThatIsNotOnTheBallotIsNotCounted()
    {
        // The vote server refuses these, but the referee is the authority and checks again —
        // a bug or a change of position upstream must not put an unplayable move on the board.
        TallyResult result = Tally(("ann", "Qh5"), ("bob", "e4"));

        Assert.That(result.Winner, Is.EqualTo("e4"));
        Assert.That(result.Voters, Is.EqualTo(1), "the impossible ballot is discarded, not counted");
    }

    [Test]
    public void OnePersonStillOnlyCountsOnce()
    {
        // The server keys ballots by browser, so this is really a check that the referee does
        // not somehow double-count a single entry.
        TallyResult result = Tally(("ann", "d4"), ("bob", "d4"));

        Assert.That(result.Counts.Single().Votes, Is.EqualTo(2));
        Assert.That(result.Voters, Is.EqualTo(2));
    }

    [Test]
    public void PlayingARound_RecordsTheCommunityMoveAndMoonforgesReply()
    {
        var state = VoteState.Empty with { Status = VoteState.Running, Game = 1, Issue = 7 };

        PlayResult played = VoteChess.Play(state, "e4", votes: 9, now: T0, hours: 24);

        Assert.Multiple(() =>
        {
            Assert.That(played.Applied, Is.True);
            Assert.That(played.State.History, Has.Length.EqualTo(2), "the community move and the reply");
            Assert.That(played.State.History[0].San, Is.EqualTo("e4"));
            Assert.That(played.State.History[0].By, Is.EqualTo(VoteMove.Community));
            Assert.That(played.State.History[0].Votes, Is.EqualTo(9));
            Assert.That(played.State.History[0].Uci, Is.EqualTo("e2e4"));
            Assert.That(played.State.History[1].By, Is.EqualTo(VoteMove.Engine));
            Assert.That(played.State.History[1].Votes, Is.Zero, "the engine does not vote");
            Assert.That(played.EngineReply, Is.Not.Empty);
            Assert.That(played.State.Fen, Is.Not.EqualTo(state.Fen), "the board moved on");
            Assert.That(played.State.Status, Is.EqualTo(VoteState.Running));
            Assert.That(played.State.DeadlineUtc, Is.Not.Empty, "the next round needs a deadline");
        });
    }

    [Test]
    public void AVoteThatIsNoLongerLegal_LeavesTheGameUntouched()
    {
        var state = VoteState.Empty with { Status = VoteState.Running };

        PlayResult played = VoteChess.Play(state, "Qh5", votes: 3, now: T0, hours: 24);

        Assert.Multiple(() =>
        {
            Assert.That(played.Applied, Is.False);
            Assert.That(played.State, Is.EqualTo(state), "nothing should change if the move cannot be made");
        });
    }

    [Test]
    public void AGameThatEndsIsMarkedFinishedWithAResult()
    {
        // Fool's mate: the community has Black and mates with Qh4.
        var state = VoteState.Empty with
        {
            Status = VoteState.Running,
            CommunityColor = "Black",
            Fen = "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq - 0 2",
        };

        PlayResult played = VoteChess.Play(state, "Qh4", votes: 5, now: T0, hours: 24);

        Assert.Multiple(() =>
        {
            Assert.That(played.Applied, Is.True);
            Assert.That(played.State.Status, Is.EqualTo(VoteState.Finished));
            Assert.That(played.State.Result, Is.EqualTo("The community wins"));
            Assert.That(played.State.DeadlineUtc, Is.Empty, "a finished game has no next deadline");
            Assert.That(played.State.History, Has.Length.EqualTo(1), "the engine gets no reply after mate");
        });
    }

    [Test]
    public void StateRoundTripsThroughJsonWithTheNamesTheSiteExpects()
    {
        var state = VoteState.Empty with
        {
            Status = VoteState.Running,
            Game = 1,
            Issue = 42,
            DeadlineUtc = "2026-08-26T12:00:00+00:00",
            History = [new VoteMove(1, "e4", "e2e4", VoteMove.Community, 7)],
        };

        string json = VoteChess.Serialise(state);
        VoteState back = VoteChess.Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"status\""), "the site reads camelCase");
            Assert.That(json, Does.Contain("\"deadlineUtc\""));
            Assert.That(back.Status, Is.EqualTo(VoteState.Running));
            Assert.That(back.IsRunning, Is.True);
            Assert.That(back.Issue, Is.EqualTo(42));
            Assert.That(back.History[0].San, Is.EqualTo("e4"));
            Assert.That(back.History[0].IsCommunity, Is.True);
            Assert.That(back.History[0].Reference, Is.EqualTo("1. e4"));
            Assert.That(back.Deadline, Is.Not.Null);
        });
    }

    [Test]
    public void TheShippedStateFileParsesAndStartsIdle()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
            dir = dir.Parent;

        string path = Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot", "vote", "state.json");
        Assert.That(File.Exists(path), Is.True, "the site fetches this file on every visit to /vote");

        VoteState state = VoteChess.Parse(File.ReadAllText(path));

        Assert.Multiple(() =>
        {
            Assert.That(state.Status, Is.EqualTo(VoteState.Idle), "no game should be running until one is launched");
            Assert.That(state.HasGame, Is.False);
            Assert.That(state.Fen, Is.EqualTo(Start));
            Assert.That(state.History, Is.Empty);
        });
    }

    // ── the ballot the referee publishes ────────────────────────────────────

    [Test]
    public void TheOpeningPositionOffersTwentyMoves()
    {
        IReadOnlyList<string> moves = VoteChess.LegalMoves(Start);

        Assert.That(moves, Has.Count.EqualTo(20), "sixteen pawn moves and four knight moves");
        Assert.That(moves, Does.Contain("e4").And.Contain("Nf3").And.Contain("a3"));
    }

    [Test]
    public void TheBallotIsSortedSoTheSamePositionAlwaysListsTheSameOrder()
    {
        IReadOnlyList<string> once = VoteChess.LegalMoves(Start);
        IReadOnlyList<string> again = VoteChess.LegalMoves(Start);

        Assert.That(once, Is.EqualTo(again));
        Assert.That(once, Is.Ordered.Using<string>(StringComparer.Ordinal));
    }

    [Test]
    public void EveryPromotionIsOfferedSeparately()
    {
        // Underpromotion is a real choice, and a vote that could only ever make a queen would
        // quietly remove it from the game.
        IReadOnlyList<string> moves = VoteChess.LegalMoves("8/4P3/8/8/8/8/8/K6k w - - 0 1");

        Assert.That(moves, Does.Contain("e8=Q").And.Contain("e8=R")
            .And.Contain("e8=B").And.Contain("e8=N"));
    }

    [Test]
    public void CastlingIsOnTheBallot()
    {
        IReadOnlyList<string> moves = VoteChess.LegalMoves("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

        Assert.That(moves, Does.Contain("O-O").And.Contain("O-O-O"));
    }

    [Test]
    public void DisambiguationMatchesWhatTheEngineWillWrite()
    {
        // Two knights reach d2. If the ballot said "Nd2" the move could never be played back,
        // so the candidate has to carry the same disambiguation the engine generates.
        IReadOnlyList<string> moves = VoteChess.LegalMoves("4k3/8/8/8/8/8/8/1N1K1N2 w - - 0 1");

        Assert.That(moves, Does.Contain("Nbd2").And.Contain("Nfd2"));
        Assert.That(moves, Does.Not.Contain("Nd2"));
    }

    [Test]
    public void EveryCandidateCanActuallyBePlayed()
    {
        // The contract that matters: whatever wins the vote must be applicable. If any
        // candidate failed here, a community vote could deadlock the game.
        foreach (string fen in new[]
        {
            Start,
            "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
            "r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1",
            "8/4P3/8/8/8/8/8/K6k w - - 0 1",
        })
        {
            foreach (string san in VoteChess.LegalMoves(fen))
            {
                Assert.That(SanMove.IsLegal(fen, san), Is.True, $"{san} in {fen}");
            }
        }
    }

    [Test]
    public void ACheckmatedSideHasAnEmptyBallot()
    {
        Assert.That(VoteChess.LegalMoves("rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3"),
            Is.Empty);
    }

    [Test]
    public void AnUnreadableFenYieldsNothingRatherThanThrowing()
    {
        Assert.That(VoteChess.LegalMoves(""), Is.Empty);
    }
}
