using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

public sealed class ChessGameSessionTests
{
    private ChessGameSession _session = null!;

    [SetUp]
    public void SetUp() => _session = new ChessGameSession();

    [TearDown]
    public void TearDown() => _session.Dispose();

    [Test]
    public void NewSession_ExposesTheStandardPosition()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_session.Fen, Is.EqualTo(ChessGameSession.StartingFen));
            Assert.That(_session.GetDisplaySquares(), Has.Count.EqualTo(64));
            Assert.That(_session.Moves, Is.Empty);
            Assert.That(_session.IsHumanTurn, Is.True);
            Assert.That(_session.Evaluation.Total, Is.EqualTo(10), "White starts with the tempo bonus");
        });
    }

    [Test]
    public async Task SelectingAPawn_OnlyHighlightsItsLegalDestinations()
    {
        await _session.ClickSquareAsync(4, 6); // e2

        string[] targets = _session.GetDisplaySquares()
            .Where(square => square.IsLegalTarget)
            .Select(square => square.Coordinate)
            .Order()
            .ToArray();

        Assert.That(targets, Is.EqualTo(new[] { "e3", "e4" }));
    }

    [Test]
    public async Task HumanMove_IsFollowedByALegalEngineReply()
    {
        await _session.ClickSquareAsync(4, 6); // e2
        await _session.ClickSquareAsync(4, 4); // e4

        Assert.Multiple(() =>
        {
            Assert.That(_session.Moves, Has.Count.EqualTo(2));
            Assert.That(_session.Moves[0].Uci, Is.EqualTo("e2e4"));
            Assert.That(_session.IsHumanTurn, Is.True);
            Assert.That(_session.Status, Does.Contain("Your move"));
            Assert.That(_session.Fen, Is.Not.EqualTo(ChessGameSession.StartingFen));
        });
    }

    [Test]
    public async Task UndoTurn_ReplaysBackToThePreviousHumanTurn()
    {
        await _session.ClickSquareAsync(4, 6);
        await _session.ClickSquareAsync(4, 4);

        _session.UndoTurn();

        Assert.Multiple(() =>
        {
            Assert.That(_session.Moves, Is.Empty);
            Assert.That(_session.Fen, Is.EqualTo(ChessGameSession.StartingFen));
            Assert.That(_session.IsHumanTurn, Is.True);
        });
    }

    [Test]
    public async Task StartingAsBlack_MakesTheOpeningEngineMove()
    {
        await _session.NewGameAsync(ChessPieceColor.Black, Engine.Difficulty.Easy);

        Assert.Multiple(() =>
        {
            Assert.That(_session.Moves, Has.Count.EqualTo(1));
            Assert.That(_session.IsHumanTurn, Is.True);
            Assert.That(_session.WhiteAtBottom, Is.False);
        });
    }

    [Test]
    public async Task Promotion_WaitsForThePlayersPieceChoice()
    {
        _session.LoadPosition("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");

        await _session.ClickSquareAsync(0, 1); // a7
        await _session.ClickSquareAsync(0, 0); // a8

        Assert.That(_session.HasPendingPromotion, Is.True);

        _session.CancelPromotion();
        Assert.That(_session.HasPendingPromotion, Is.False);
        Assert.That(_session.Moves, Is.Empty);
    }

    [Test]
    public void LoadPosition_RejectsMalformedFen()
    {
        Assert.That(
            () => _session.LoadPosition("not a chess position"),
            Throws.InstanceOf<FormatException>());
    }

    [Test]
    public void FlipBoard_DoesNotChangeWhichSideTheHumanControls()
    {
        _session.FlipBoard();

        Assert.Multiple(() =>
        {
            Assert.That(_session.WhiteAtBottom, Is.False);
            Assert.That(_session.HumanColor, Is.EqualTo(ChessPieceColor.White));
            Assert.That(_session.IsHumanTurn, Is.True);
        });
    }
}
