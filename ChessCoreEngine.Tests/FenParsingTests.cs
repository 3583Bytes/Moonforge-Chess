using System;
using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

[TestFixture]
public class FenParsingTests
{
    [TestCase("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [TestCase("4k3/8/8/8/8/8/4P3/4K3 w - - 100 42")]
    [TestCase("r3k2r/8/8/8/8/8/8/R3K2R b Kq e3 70 1234")]
    [TestCase("8/8/8/8/8/8/8/8 w - - 0 0")]
    [TestCase("8/8/8/8/8/8/8/8 w - - 255 2147483647")]
    public void FullFen_RoundTripsWithoutLosingFields(string fen)
    {
        var board = new Board(fen);

        Assert.That(Board.Fen(false, board), Is.EqualTo(fen));
    }

    [Test]
    public void FourFieldFen_RemainsSupported()
    {
        const string fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b - -";

        var board = new Board(fen);

        Assert.That(Board.Fen(true, board), Is.EqualTo(fen));
        Assert.That(board.HalfMoveClock, Is.Zero);
        Assert.That(board.MoveCount, Is.Zero);
    }

    [TestCase("4k3/8/8/8/8/8/4P3/4K3 b - e3 17 30", 44, ChessPieceColor.White)]
    [TestCase("4k3/8/8/8/8/8/4P3/4K3 w - e6 17 30", 20, ChessPieceColor.Black)]
    public void EnPassant_UsesOnlyTheValidatedTargetField(
        string fen, int expectedSquare, ChessPieceColor expectedInitiator)
    {
        var board = new Board(fen);
        Position position = Position.FromFen(fen);

        Assert.Multiple(() =>
        {
            Assert.That(board.EnPassantPosition, Is.EqualTo(expectedSquare));
            Assert.That(board.EnPassantColor, Is.EqualTo(expectedInitiator));
            Assert.That(position.EpSquare, Is.EqualTo(expectedSquare));
        });
    }

    [Test]
    public void CastlingRights_SetOnlyTheMatchingKingAndRookUnmoved()
    {
        var board = new Board("r3k2r/8/8/8/8/8/8/R3K2R w Kq - 0 1");

        Assert.Multiple(() =>
        {
            Assert.That(board.WhiteCastled, Is.False);
            Assert.That(board.BlackCastled, Is.False);
            Assert.That(board.Squares[60].Piece.Moved, Is.False, "white king");
            Assert.That(board.Squares[63].Piece.Moved, Is.False, "h1 rook");
            Assert.That(board.Squares[56].Piece.Moved, Is.True, "a1 rook");
            Assert.That(board.Squares[4].Piece.Moved, Is.False, "black king");
            Assert.That(board.Squares[0].Piece.Moved, Is.False, "a8 rook");
            Assert.That(board.Squares[7].Piece.Moved, Is.True, "h8 rook");
        });
    }

    [Test]
    public void MailboxAndBitboardParsers_ProduceTheSameState()
    {
        const string fen = "r3k2r/8/8/3pP3/8/8/8/R3K2R b Kq e3 100 1234";
        var board = new Board(fen);
        Position position = Position.FromFen(fen);

        Assert.Multiple(() =>
        {
            Assert.That(position.SideToMove, Is.EqualTo((int)board.WhoseMove));
            Assert.That(position.EpSquare, Is.EqualTo(board.EnPassantPosition));
            Assert.That(position.HalfmoveClock, Is.EqualTo(board.HalfMoveClock));
            Assert.That(position.FullMove, Is.EqualTo(board.MoveCount));
            Assert.That(position.CastleRights,
                Is.EqualTo(FenParser.WhiteKingSide | FenParser.BlackQueenSide));

            for (int square = 0; square < 64; square++)
            {
                Piece piece = board.Squares[square].Piece;
                byte expected = piece == null
                    ? Position.EMPTY
                    : (byte)Position.PieceIndex(piece.PieceColor, piece.PieceType);
                Assert.That(position.PieceOn[square], Is.EqualTo(expected), $"square {square}");
            }
        });
    }

    [Test]
    public void ExtraWhitespace_IsAcceptedAndCanonicalized()
    {
        var board = new Board("  4k3/8/8/8/8/8/8/4K3\t b   -  -  12  34  ");

        Assert.That(Board.Fen(false, board),
            Is.EqualTo("4k3/8/8/8/8/8/8/4K3 b - - 12 34"));
    }

    [TestCase("")]
    [TestCase("8/8/8/8/8/8/8/8")]
    [TestCase("8/8/8/8/8/8/8/8 w - - 0")]
    [TestCase("8/8/8/8/8/8/8 w - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/8/8 w - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/7 w - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/44 w - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/9 w - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/7X w - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 x - - 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 w KK - 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 w A - 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 w - e4 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 w - e3 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 b - e6 0 1")]
    [TestCase("8/8/8/8/8/8/8/8 w - - -1 1")]
    [TestCase("8/8/8/8/8/8/8/8 w - - 256 1")]
    [TestCase("8/8/8/8/8/8/8/8 w - - 0 nope")]
    public void MalformedFen_IsRejectedByBothRepresentations(string fen)
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new Board(fen), Throws.TypeOf<FormatException>());
            Assert.That(() => Position.FromFen(fen), Throws.TypeOf<FormatException>());
        });
    }

    [Test]
    public void NullFen_IsRejectedExplicitly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new Board((string)null!), Throws.TypeOf<ArgumentNullException>());
            Assert.That(() => Position.FromFen((string)null!), Throws.TypeOf<ArgumentNullException>());
        });
    }
}
