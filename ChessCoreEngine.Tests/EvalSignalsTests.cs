using System.Collections.Generic;
using System.Text;
using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

// Validates that BitboardEvalNative.ComputeSignals reproduces the per-square
// signals GenerateValidMoves produces — attack/defend values, mobility, and the
// attack maps — exactly, across positions reached by making moves. This is the
// foundation for the native (no-Board) evaluator.
[TestFixture]
public class EvalSignalsTests
{
    [OneTimeSetUp]
    public void Init() => PieceMoves.InitiateChessPieceMotion();

    [TestCase("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 3)]
    [TestCase("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)] // Kiwipete
    [TestCase("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 3)]                            // Position 3 (ep)
    [TestCase("rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 2)]            // Position 5 (promo)
    [TestCase("r3k2r/pppq1ppp/2n1bn2/3pp3/3PP3/2N1BN2/PPPQ1PPP/R3K2R w KQkq - 0 1", 2)]   // both can castle
    public void Signals_MatchGenerateValidMoves(string fen, int depth)
    {
        var pos = Position.FromFen(fen);
        Walk(pos, depth);
    }

    private static void Walk(Position pos, int depth)
    {
        AssertSignals(pos);
        AssertScore(pos);
        if (depth == 0) return;
        var moves = new List<Move>(64);
        MoveGen.GenerateLegal(pos, moves);
        foreach (Move m in moves)
        {
            pos.MakeMove(m);
            Walk(pos, depth - 1);
            pos.UnmakeMove(m);
        }
    }

    // The native evaluator must match the legacy (fresh-load) score exactly.
    private static void AssertScore(Position pos)
    {
        int legacy = BitboardEval.ScoreViaLegacyGen(pos);
        int native = BitboardEvalNative.Score(pos);
        if (native != legacy)
            Assert.Fail($"score mismatch at FEN {Board.Fen(false, BitboardEval.ToBoard(pos))}: native={native} legacy={legacy}");
    }

    private static void AssertSignals(Position pos)
    {
        // Oracle: GenerateValidMoves on the projected board.
        Board b = BitboardEval.ToBoard(pos);
        PieceValidMoves.GenerateValidMoves(b);

        var sig = BitboardEvalNative.ComputeSignals(pos);

        var sb = new StringBuilder();
        for (int sq = 0; sq < 64; sq++)
        {
            // Attack maps.
            if (sig.WhiteAtt[sq] != b.WhiteAttackBoard[sq])
                sb.AppendLine($"  sq{sq} WhiteAtt: native={sig.WhiteAtt[sq]} gvm={b.WhiteAttackBoard[sq]}");
            if (sig.BlackAtt[sq] != b.BlackAttackBoard[sq])
                sb.AppendLine($"  sq{sq} BlackAtt: native={sig.BlackAtt[sq]} gvm={b.BlackAttackBoard[sq]}");

            var piece = b.Squares[sq].Piece;
            if (piece == null) continue;
            int gMob = piece.ValidMoves?.Count ?? 0;
            if (sig.Mobility[sq] != gMob)
                sb.AppendLine($"  sq{sq} Mobility: native={sig.Mobility[sq]} gvm={gMob} ({piece.PieceColor} {piece.PieceType})");
            if (sig.Attacked[sq] != piece.AttackedValue)
                sb.AppendLine($"  sq{sq} Attacked: native={sig.Attacked[sq]} gvm={piece.AttackedValue} ({piece.PieceColor} {piece.PieceType})");
            if (sig.Defended[sq] != piece.DefendedValue)
                sb.AppendLine($"  sq{sq} Defended: native={sig.Defended[sq]} gvm={piece.DefendedValue} ({piece.PieceColor} {piece.PieceType})");
        }

        if (sb.Length > 0)
            Assert.Fail($"signal mismatch at FEN {Board.Fen(false, b)}\n{sb}");
    }
}
