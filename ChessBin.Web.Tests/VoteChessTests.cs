using ChessBin.Web;

namespace ChessBin.Web.Tests;

public sealed class VoteChessTests
{
    private const string Start = ChessGameSession.StartingFen;
    private static readonly DateTimeOffset T0 = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static VoteComment C(string author, string body, int minutes) =>
        new(author, body, T0.AddMinutes(minutes));

    private static TallyResult Tally(params VoteComment[] comments) =>
        VoteChess.Tally(Start, comments, botLogin: "chessbin-bot", windowStart: T0);

    [Test]
    public void PeopleCanWriteAMoveInASentence()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VoteChess.FindMove(Start, "I say e4"), Is.EqualTo("e4"));
            Assert.That(VoteChess.FindMove(Start, "**Nf3** looks best to me"), Is.EqualTo("Nf3"));
            Assert.That(VoteChess.FindMove(Start, "d4!"), Is.EqualTo("d4"));
            Assert.That(VoteChess.FindMove(Start, "let's go with `c4`."), Is.EqualTo("c4"));
            Assert.That(VoteChess.FindMove(Start, "no idea honestly"), Is.Null);
            Assert.That(VoteChess.FindMove(Start, "Qh5 is illegal here"), Is.Null,
                "a move that isn't legal in this position is not a vote");
        });
    }

    [Test]
    public void TheMostVotedMoveWins()
    {
        var result = Tally(
            C("ann", "e4", 1),
            C("bob", "e4", 2),
            C("cal", "d4", 3));

        Assert.Multiple(() =>
        {
            Assert.That(result.Winner, Is.EqualTo("e4"));
            Assert.That(result.Voters, Is.EqualTo(3));
            Assert.That(result.Counts[0].Votes, Is.EqualTo(2));
            Assert.That(result.Counts, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void OnePersonGetsOneVoteAndMayChangeTheirMind()
    {
        var result = Tally(
            C("ann", "e4", 1),
            C("ann", "actually d4", 5),      // same person, later
            C("bob", "d4", 2));

        Assert.Multiple(() =>
        {
            Assert.That(result.Winner, Is.EqualTo("d4"));
            Assert.That(result.Voters, Is.EqualTo(2), "ann still only counts once");
            Assert.That(result.Counts.Single(c => c.San == "d4").Votes, Is.EqualTo(2));
            Assert.That(result.Counts.Any(c => c.San == "e4"), Is.False, "her earlier vote is gone");
        });
    }

    [Test]
    public void TiesGoToTheMoveProposedFirst()
    {
        var result = Tally(
            C("ann", "d4", 5),
            C("bob", "e4", 1),
            C("cal", "d4", 6),
            C("dee", "e4", 2));

        Assert.That(result.Winner, Is.EqualTo("e4"),
            "e4 and d4 both have two votes, but e4 was on the table first");
    }

    [Test]
    public void TheBotsOwnCommentsAndAnythingBeforeTheWindowAreIgnored()
    {
        var result = VoteChess.Tally(Start,
        [
            new VoteComment("chessbin-bot", "Voting is open. Reply with a move.", T0.AddMinutes(1)),
            new VoteComment("ann", "e4", T0.AddMinutes(-30)),   // last round's vote
            new VoteComment("bob", "d4", T0.AddMinutes(3)),
        ], botLogin: "chessbin-bot", windowStart: T0);

        Assert.Multiple(() =>
        {
            Assert.That(result.Winner, Is.EqualTo("d4"));
            Assert.That(result.Voters, Is.EqualTo(1), "the bot does not vote and stale comments do not carry over");
        });
    }

    [Test]
    public void NobodyVoting_LeavesNoWinnerRatherThanPickingOne()
    {
        var result = Tally(C("ann", "hello", 1), C("bob", "good luck everyone", 2));

        Assert.Multiple(() =>
        {
            Assert.That(result.HasWinner, Is.False);
            Assert.That(result.Winner, Is.Null);
            Assert.That(result.Voters, Is.Zero);
            Assert.That(result.Counts, Is.Empty);
        });
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
}
