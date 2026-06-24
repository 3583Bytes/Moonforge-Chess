using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

// Orientation sanity checks for the bitboard attack tables. Square indexing is
// the engine's: 0 = a8, 63 = h1; file = sq % 8, row = sq / 8 (0 = rank 8).
// These guard the geometry that the whole bitboard move generator is built on.
[TestFixture]
public class BitboardTests
{
    private const int A8 = 0, B8 = 1, H8 = 7, A1 = 56, D4 = 35, E4 = 36, A4 = 32, H4 = 39;

    private static ulong Mask(params int[] squares)
    {
        ulong b = 0;
        foreach (var s in squares) b |= 1UL << s;
        return b;
    }

    [Test]
    public void KnightFromCorner_A8_AttacksTwoSquares()
    {
        // a8 knight reaches b6 (file1,row2 = 17) and c7 (file2,row1 = 10).
        Assert.That(Bitboards.KnightAttacks[A8], Is.EqualTo(Mask(10, 17)));
    }

    [Test]
    public void KnightFromCenter_D4_AttacksEight()
    {
        Assert.That(Bitboards.PopCount(Bitboards.KnightAttacks[D4]), Is.EqualTo(8));
    }

    [Test]
    public void KingFromCorner_A8_AttacksThreeSquares()
    {
        // a8 king reaches b8 (1), a7 (8), b7 (9).
        Assert.That(Bitboards.KingAttacks[A8], Is.EqualTo(Mask(1, 8, 9)));
    }

    [Test]
    public void WhitePawnCaptures_E4_AttackDiagonallyTowardRank8()
    {
        // White pawn on e4 (file4,row4=36) attacks d5 (file3,row3=27) and f5 (file5,row3=29).
        Assert.That(Bitboards.PawnAttacks[0][E4], Is.EqualTo(Mask(27, 29)));
    }

    [Test]
    public void BlackPawnCaptures_E4_AttackDiagonallyTowardRank1()
    {
        // Black pawn on e4 attacks d3 (file3,row5=43) and f3 (file5,row5=45).
        Assert.That(Bitboards.PawnAttacks[1][E4], Is.EqualTo(Mask(43, 45)));
    }

    [Test]
    public void RookOnEmptyBoard_A8_Attacks14Squares()
    {
        ulong att = Bitboards.RookAttacks(A8, 0);
        Assert.That(Bitboards.PopCount(att), Is.EqualTo(14));
        // No wraparound: h1 (63) must not be attacked from a8.
        Assert.That(att & (1UL << 63), Is.EqualTo(0UL));
    }

    [Test]
    public void RookBlocked_StopsAtAndIncludesBlocker()
    {
        // Rook on a4 (32), blocker on d4 (35) along the east ray.
        ulong occ = 1UL << D4;
        ulong att = Bitboards.RookAttacks(A4, occ);
        Assert.That(att & (1UL << 33), Is.Not.EqualTo(0UL)); // b4 reachable
        Assert.That(att & (1UL << 34), Is.Not.EqualTo(0UL)); // c4 reachable
        Assert.That(att & (1UL << D4), Is.Not.EqualTo(0UL)); // d4 (blocker) included
        Assert.That(att & (1UL << 36), Is.EqualTo(0UL));     // e4 cut off (beyond blocker)
    }

    [Test]
    public void BishopOnEmptyBoard_CenterAttacks13Squares()
    {
        Assert.That(Bitboards.PopCount(Bitboards.BishopAttacks(D4, 0)), Is.EqualTo(13));
    }

    [Test]
    public void QueenOnEmptyBoard_CenterAttacks27Squares()
    {
        Assert.That(Bitboards.PopCount(Bitboards.QueenAttacks(D4, 0)), Is.EqualTo(27));
    }
}
