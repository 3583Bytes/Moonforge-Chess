using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

// Static Exchange Evaluation sanity checks on hand-computed positions.
// Square index (engine convention): (8 - rank) * 8 + file, file a=0..h=7.
[TestFixture]
public class SeeTests
{
    private static byte Sq(string s) => (byte)((8 - (s[1] - '0')) * 8 + (s[0] - 'a'));

    private static int See(string fen, string from, string to)
        => MoveGen.See(Position.FromFen(fen), new Move(Sq(from), Sq(to)));

    [Test]
    public void CleanPawnCapture_WinsAPawn()
    {
        // White Pe4 x Pd5 (undefended) → +100.
        Assert.That(See("k7/8/8/3p4/4P3/8/8/7K w - - 0 1", "e4", "d5"), Is.EqualTo(100));
    }

    [Test]
    public void RookTakesPawnDefendedByPawn_LosesMaterial()
    {
        // White Rd2 x Pd5, recaptured by e6 pawn → +100 − 500 = −400.
        Assert.That(See("k7/8/4p3/3p4/8/8/3R4/7K w - - 0 1", "d2", "d5"), Is.EqualTo(-400));
    }

    [Test]
    public void PawnTakesKnightDefendedByPawn_StillWins()
    {
        // White Pe4 x Nd5 (defended by e6 pawn): +320 − 100 = +220.
        Assert.That(See("k7/8/4p3/3n4/4P3/8/8/7K w - - 0 1", "e4", "d5"), Is.EqualTo(220));
    }
}
