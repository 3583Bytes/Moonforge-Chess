using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

[TestFixture]
public class PerftBaselineTests
{
    private static readonly string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [TestCase(1, 20)]
    [TestCase(2, 400)]
    [TestCase(3, 8902)]
    [TestCase(4, 197281)]
    public void InitialPosition_PerftMatchesKnownCounts(int depth, long expectedNodes)
    {
        var engine = new Engine(InitialFen);
        var result = engine.RunPerformanceTest(depth);

        Assert.That(result.Nodes, Is.EqualTo(expectedNodes));
    }

    [Test]
    [Category("Slow")]
    public void InitialPosition_PerftDepth5_MatchesKnownCount()
    {
        var engine = new Engine(InitialFen);
        var result = engine.RunPerformanceTest(5);

        Assert.That(result.Nodes, Is.EqualTo(4865609));
    }

    // Standard perft positions from the Chess Programming Wiki. These exercise the
    // move-generation paths the initial position does not: en passant captures,
    // promotions, castling rights/legality, and pins. Node counts are absolute,
    // community-verified truths and must hold regardless of board representation.

    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string Position3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    private const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    private const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    private const string Position6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    [TestCase(Kiwipete, 1, 48)]
    [TestCase(Kiwipete, 2, 2039)]
    [TestCase(Kiwipete, 3, 97862)]
    [TestCase(Position3, 1, 14)]
    [TestCase(Position3, 2, 191)]
    [TestCase(Position3, 3, 2812)]
    [TestCase(Position3, 4, 43238)]
    [TestCase(Position4, 1, 6)]
    [TestCase(Position4, 2, 264)]
    [TestCase(Position4, 3, 9467)]
    [TestCase(Position5, 1, 44)]
    [TestCase(Position5, 2, 1486)]
    [TestCase(Position5, 3, 62379)]
    [TestCase(Position6, 1, 46)]
    [TestCase(Position6, 2, 2079)]
    [TestCase(Position6, 3, 89890)]
    public void StandardPositions_PerftMatchesKnownCounts(string fen, int depth, long expectedNodes)
    {
        var engine = new Engine(fen);
        var result = engine.RunPerformanceTest(depth);

        Assert.That(result.Nodes, Is.EqualTo(expectedNodes));
    }

}
