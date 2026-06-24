using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

// Perft validation for the bitboard core (Position + MoveGen). Node counts are
// the community-verified standard values. Position 3 — the en-passant /
// discovered-check test the old mailbox generator gets wrong — is included and
// MUST pass here.
[TestFixture]
public class BitboardPerftTests
{
    private const string Initial = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    private const string Position3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    private const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    private const string Position5 = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    private const string Position6 = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    [TestCase(Initial, 1, 20)]
    [TestCase(Initial, 2, 400)]
    [TestCase(Initial, 3, 8902)]
    [TestCase(Initial, 4, 197281)]
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
    public void Perft_MatchesKnownCounts(string fen, int depth, long expected)
    {
        var p = Position.FromFen(fen);
        Assert.That(MoveGen.Perft(p, depth), Is.EqualTo(expected));
    }

    [TestCase(Kiwipete, 4, 4085603)]
    [TestCase(Position3, 5, 674624)]
    [TestCase(Position4, 4, 422333)]
    [TestCase(Position5, 4, 2103487)]
    [TestCase(Position6, 4, 3894594)]
    [TestCase(Initial, 5, 4865609)]
    [Category("Slow")]
    public void Perft_DeepMatchesKnownCounts(string fen, int depth, long expected)
    {
        var p = Position.FromFen(fen);
        Assert.That(MoveGen.Perft(p, depth), Is.EqualTo(expected));
    }

    // The incremental Zobrist hash maintained through make/unmake must always
    // equal a from-scratch recomputation. Walks the full move tree to a shallow
    // depth, asserting consistency at every node.
    [TestCase(Initial, 4)]
    [TestCase(Kiwipete, 3)]
    [TestCase(Position3, 4)]
    [TestCase(Position5, 3)]
    public void IncrementalHash_MatchesFromScratch(string fen, int depth)
    {
        var p = Position.FromFen(fen);
        Assert.That(p.Hash, Is.EqualTo(p.ComputeHash()), "FEN load hash mismatch");
        WalkAssertingHash(p, depth);
    }

    private static void WalkAssertingHash(Position p, int depth)
    {
        if (depth == 0) return;
        var moves = new System.Collections.Generic.List<Move>(64);
        MoveGen.GenerateLegal(p, moves);
        foreach (Move m in moves)
        {
            p.MakeMove(m);
            Assert.That(p.Hash, Is.EqualTo(p.ComputeHash()), $"hash mismatch after {m}");
            WalkAssertingHash(p, depth - 1);
            p.UnmakeMove(m);
            Assert.That(p.Hash, Is.EqualTo(p.ComputeHash()), $"hash mismatch after unmake {m}");
        }
    }
}
