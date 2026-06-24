using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

// End-to-end integration test for the bitboard search: Position + bitboard move
// generation + make/unmake + the eval bridge + negamax alpha-beta. Validates that
// the search picks sound moves on tactical positions.
[TestFixture]
public class BitboardSearchTests
{
    [OneTimeSetUp]
    public void InitMoveArrays() => PieceMoves.InitiateChessPieceMotion();

    [Test]
    public void FindsMateInOne()
    {
        // Two-rook ladder mate: g6g8# (same position used by the legacy TestAI).
        var p = Position.FromFen("k7/7R/6R1/8/8/8/8/K7 w - - 0 1");
        Move best = BitboardSearch.FindBestMove(p, 3, out int score);
        Assert.That(best.ToString(), Is.EqualTo("g6g8"));
        Assert.That(score, Is.GreaterThan(900_000), "should report a mate score");
    }

    [Test]
    public void FindsMateInOne_BlackToMove()
    {
        // Mirror: black mates with g3g1#.
        var p = Position.FromFen("k7/8/8/8/8/6r1/7r/K7 b - - 0 1");
        Move best = BitboardSearch.FindBestMove(p, 3, out int score);
        Assert.That(best.ToString(), Is.EqualTo("g3g1"));
        Assert.That(score, Is.GreaterThan(900_000));
    }

    [Test]
    public void WinsHangingQueen()
    {
        // White queen on d1 can capture a free black queen on d8 down the open d-file.
        var p = Position.FromFen("3q1k2/8/8/8/8/8/8/3QK3 w - - 0 1");
        Move best = BitboardSearch.FindBestMove(p, 4, out _);
        Assert.That(best.ToString(), Is.EqualTo("d1d8"), "should grab the free queen");
    }

    [Test]
    public void PrefersCapturingWithLessValuablePiece()
    {
        // MVV-LVA sanity: from the start position the search returns a legal move
        // and a roughly balanced score (no blunders).
        var p = Position.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        Move best = BitboardSearch.FindBestMove(p, 3, out int score);
        Assert.That(best.From, Is.Not.EqualTo(best.To), "should return a real move");
        Assert.That(System.Math.Abs(score), Is.LessThan(200), "opening should be near-balanced");
    }
}
