using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

[TestFixture]
public class EvaluationBreakdownTests
{
    private static readonly string[] Fens =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "1Q6/5pk1/2p3p1/1p2N2p/1b5P/1bn5/2r3P1/2K5 w - - 16 42",
    };

    [Test]
    public void Total_MatchesNativeAndLegacyScores_AcrossBattery()
    {
        foreach (string fen in Fens)
        {
            Position position = Position.FromFen(fen);
            EvaluationBreakdown detail = BitboardEvalNative.DetailedScore(position);

            Assert.Multiple(() =>
            {
                Assert.That(detail.Total, Is.EqualTo(BitboardEvalNative.Score(position)), fen);
                Assert.That(detail.Total, Is.EqualTo(BitboardEval.ScoreViaLegacyGen(position)), fen);
            });
        }
    }

    [Test]
    public void StartingPosition_IsBalancedExceptForTempo()
    {
        Position position = Position.FromFen(Fens[0]);
        EvaluationBreakdown detail = BitboardEvalNative.DetailedScore(position);

        Assert.Multiple(() =>
        {
            Assert.That(detail.Material, Is.Zero);
            Assert.That(detail.Tempo, Is.EqualTo(10));
            Assert.That(detail.Total, Is.EqualTo(10));
        });
    }

    [Test]
    public void InsufficientMaterial_ExplainsDrawOverride()
    {
        Position position = Position.FromFen("8/8/8/4k3/8/8/3K1N2/8 b - - 0 1");
        EvaluationBreakdown detail = BitboardEvalNative.DetailedScore(position);

        Assert.Multiple(() =>
        {
            Assert.That(detail.DrawReason, Is.EqualTo("insufficient material"));
            Assert.That(detail.DrawAdjustment, Is.Not.Zero);
            Assert.That(detail.Total, Is.Zero);
        });
    }

    [Test]
    public void FiftyMoveRule_ExplainsDraw()
    {
        Position position = Position.FromFen("4k3/8/8/8/8/8/4P3/4K3 w - - 100 1");
        EvaluationBreakdown detail = BitboardEvalNative.DetailedScore(position);

        Assert.Multiple(() =>
        {
            Assert.That(detail.DrawReason, Is.EqualTo("50-move rule"));
            Assert.That(detail.Total, Is.Zero);
        });
    }

    [Test]
    public void EngineBreakdown_PreservesActualCastlingHistory()
    {
        var engine = new Engine("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");

        Assert.That(engine.MovePieceAN("e1g1"), Is.True);
        EvaluationBreakdown detail = engine.GetEvaluationBreakdown();

        Assert.Multiple(() =>
        {
            Assert.That(detail.Castling, Is.EqualTo(50));
            Assert.That(detail.Total, Is.EqualTo(engine.EvaluateBoardScore()));
        });
    }

    [Test]
    public void EngineBreakdown_RecognizesFiftyMoveDrawFromSparseFen()
    {
        var engine = new Engine("4k3/8/8/8/8/8/4P3/4K3 w - - 100 42");

        EvaluationBreakdown detail = engine.GetEvaluationBreakdown();

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetHalfMoveClock(), Is.EqualTo(100));
            Assert.That(engine.FEN, Does.EndWith("100 42"));
            Assert.That(detail.DrawReason, Is.EqualTo("50-move rule"));
            Assert.That(detail.Total, Is.Zero);
        });
    }
}
