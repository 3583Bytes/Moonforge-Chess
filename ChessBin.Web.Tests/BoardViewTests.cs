using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

/// <summary>
/// The game, the puzzle and the analysis page all render through this, so a mistake here
/// breaks three features at once.
/// </summary>
public sealed class BoardViewTests
{
    private static Engine At(string fen)
    {
        var e = new Engine(fen);
        e.GenerateValidMoves();
        return e;
    }

    private const string Start = ChessGameSession.StartingFen;

    [Test]
    public void ItProducesSixtyFourSquaresWithThePiecesInTheRightPlaces()
    {
        var squares = BoardView.Squares(At(Start), whiteAtBottom: true);

        Assert.Multiple(() =>
        {
            Assert.That(squares, Has.Count.EqualTo(64));
            Assert.That(squares.Count(s => s.IsOccupied), Is.EqualTo(32));
            Assert.That(squares.First().Coordinate, Is.EqualTo("a8"), "white at the bottom starts from a8");
            Assert.That(squares.Last().Coordinate, Is.EqualTo("h1"));
            Assert.That(squares.First().PieceType, Is.EqualTo(ChessPieceType.Rook));
            Assert.That(squares.First().PieceColor, Is.EqualTo(ChessPieceColor.Black));
        });
    }

    [Test]
    public void FlippingTheBoardReversesTheReadingOrderButNotThePosition()
    {
        var white = BoardView.Squares(At(Start), whiteAtBottom: true);
        var black = BoardView.Squares(At(Start), whiteAtBottom: false);

        Assert.Multiple(() =>
        {
            Assert.That(black.First().Coordinate, Is.EqualTo("h1"), "black at the bottom starts from h1");
            Assert.That(black.Last().Coordinate, Is.EqualTo("a8"));
            // Same position either way: a square keeps its occupant regardless of orientation.
            foreach (var s in black)
            {
                var same = white.Single(w => w.Coordinate == s.Coordinate);
                Assert.That(s.PieceType, Is.EqualTo(same.PieceType), s.Coordinate);
                Assert.That(s.PieceColor, Is.EqualTo(same.PieceColor), s.Coordinate);
                Assert.That(s.IsDark, Is.EqualTo(same.IsDark), $"{s.Coordinate} must keep its colour");
            }
        });
    }

    [Test]
    public void TheReadOnlyOverloadHighlightsOnlyTheLastMove()
    {
        // e2-e4 played: e2 is index 52, e4 is index 36.
        var squares = BoardView.Squares(
            At("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1"),
            whiteAtBottom: true, lastFrom: 52, lastTo: 36);

        Assert.Multiple(() =>
        {
            Assert.That(squares.Where(s => s.IsLastMove).Select(s => s.Coordinate),
                Is.EquivalentTo(new[] { "e2", "e4" }));
            Assert.That(squares.Any(s => s.IsSelected), Is.False, "a read-only board selects nothing");
            Assert.That(squares.Any(s => s.IsLegalTarget), Is.False, "and offers no targets");
        });
    }

    [Test]
    public void SelectionAndTargetsComeFromThePredicates()
    {
        var squares = BoardView.Squares(At(Start), whiteAtBottom: true,
            isSelected: i => i == 52,                  // e2
            isLegalTarget: i => i is 44 or 36,         // e3, e4
            isLastMove: _ => false);

        Assert.Multiple(() =>
        {
            Assert.That(squares.Single(s => s.IsSelected).Coordinate, Is.EqualTo("e2"));
            Assert.That(squares.Where(s => s.IsLegalTarget).Select(s => s.Coordinate),
                Is.EquivalentTo(new[] { "e3", "e4" }));
        });
    }

    [Test]
    public void SquareColoursAlternateCorrectly()
    {
        var squares = BoardView.Squares(At(Start), whiteAtBottom: true);

        Assert.Multiple(() =>
        {
            // a8 is a light square on a real board; h1 is light too.
            Assert.That(squares.Single(s => s.Coordinate == "a8").IsDark, Is.False);
            Assert.That(squares.Single(s => s.Coordinate == "h1").IsDark, Is.False);
            Assert.That(squares.Single(s => s.Coordinate == "a1").IsDark, Is.True);
            Assert.That(squares.Count(s => s.IsDark), Is.EqualTo(32));
        });
    }
}
