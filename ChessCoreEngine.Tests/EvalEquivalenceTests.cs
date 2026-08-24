using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

// Phase-2 hard gate: the bitboard-backed scorer (BitboardEval.ScoreViaLegacyGen,
// which projects a Position onto a Board and runs the real generator + evaluator)
// must produce EXACTLY the same score as the legacy path on the same position.
// Covers a diverse battery: opening, tactical middlegames, castled kings, en
// passant, promotions, and endgames.
[TestFixture]
public class EvalEquivalenceTests
{
    private static readonly string[] Fens =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",                       // start
        "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2",                  // ep target, white to move
        "rnbqkbnr/pp1ppppp/8/2p5/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2",                 // black to move
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",           // Kiwipete (rights both sides)
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",                                      // Position 3, no rights, endgame
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",                      // Position 5, white rights only, promo pawn
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",       // both castled, no rights
        "r2q1rk1/ppp1ppbp/5np1/8/2PP1B2/1QN2N2/PP2PPPP/R3KB1R b KQ - 1 6",                // black castled, white rights
        "1Q6/5pk1/2p3p1/1p2N2p/1b5P/1bn5/2r3P1/2K5 w - - 16 42",                          // late endgame, few pieces
        "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",                                                // K+P vs K (insufficient-ish)
        "8/8/8/4k3/8/8/3K1N2/8 b - - 0 1",                                                // K+N vs K (insufficient material)
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",              // Position 4, black rights only
        "8/PPPk4/8/8/8/8/4Kppp/8 w - - 0 1",                                              // promotion race
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1",                       // start, black to move
        "r3k2r/p2pqpb1/bn1ppnp1/4N3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 2",            // lockstep failure #1 (fresh-load check)
        "r3k2r/pppq1ppp/2n1b3/3np3/3PP3/4BN2/PPPQ1PPP/R3K2R w KQkq - 0 2",                // lockstep failure #2 (fresh-load check)
    };

    [OneTimeSetUp]
    public void InitMoveArrays()
    {
        // The legacy GenerateValidMoves relies on PieceMoves' precomputed move
        // tables, normally initialized by the Engine constructor. These tests call
        // it directly, so initialize them here.
        PieceMoves.InitiateChessPieceMotion();
    }

    [Test]
    public void BitboardScorer_MatchesLegacyScore_AcrossBattery()
    {
        foreach (string fen in Fens)
        {
            int legacy = LegacyScore(fen);
            int bitboard = BitboardEval.ScoreViaLegacyGen(Position.FromFen(fen));
            Assert.That(bitboard, Is.EqualTo(legacy), $"score mismatch for FEN: {fen}");
        }
    }

    // The legacy path exactly as the engine scores a freshly loaded position
    // (see Engine.InitiateBoard).
    private static int LegacyScore(string fen)
    {
        var board = new Board(fen);
        PieceValidMoves.GenerateValidMoves(board);
        Evaluation.EvaluateBoardScore(board);
        return board.Score;
    }

    [TestCase("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [TestCase("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    public void GenerateValidMoves_IsIdempotent(string fen)
    {
        var board = new Board(fen);
        PieceValidMoves.GenerateValidMoves(board);
        Evaluation.EvaluateBoardScore(board);

        int firstScore = board.Score;
        bool[] firstWhiteAttacks = (bool[])board.WhiteAttackBoard.Clone();
        bool[] firstBlackAttacks = (bool[])board.BlackAttackBoard.Clone();
        var firstAttackedValues = new short[64];
        var firstDefendedValues = new short[64];
        var firstValidMoves = new byte[64][];
        for (int square = 0; square < board.Squares.Length; square++)
        {
            var piece = board.Squares[square].Piece;
            if (piece == null)
                continue;

            firstAttackedValues[square] = piece.AttackedValue;
            firstDefendedValues[square] = piece.DefendedValue;
            firstValidMoves[square] = piece.ValidMoves.ToArray();
        }

        PieceValidMoves.GenerateValidMoves(board);
        Evaluation.EvaluateBoardScore(board);

        Assert.Multiple(() =>
        {
            Assert.That(board.Score, Is.EqualTo(firstScore));
            Assert.That(board.WhiteAttackBoard, Is.EqualTo(firstWhiteAttacks));
            Assert.That(board.BlackAttackBoard, Is.EqualTo(firstBlackAttacks));

            for (int square = 0; square < board.Squares.Length; square++)
            {
                var piece = board.Squares[square].Piece;
                if (firstValidMoves[square] == null)
                {
                    Assert.That(piece, Is.Null, $"piece appeared on square {square}");
                    continue;
                }

                Assert.That(piece, Is.Not.Null, $"piece disappeared from square {square}");
                Assert.That(piece!.AttackedValue, Is.EqualTo(firstAttackedValues[square]),
                    $"attacked value changed on square {square}");
                Assert.That(piece.DefendedValue, Is.EqualTo(firstDefendedValues[square]),
                    $"defended value changed on square {square}");
                Assert.That(piece.ValidMoves, Is.EqualTo(firstValidMoves[square]),
                    $"valid moves changed on square {square}");
            }
        });
    }

    // Stronger gate: drive the bitboard Position and a legacy Board in lockstep
    // through the same moves and assert scores stay identical at every node. This
    // exercises path-dependent state (notably the Castled flag, which the
    // fresh-FEN battery can't fully cover) and cross-checks Position.MakeMove
    // against Board.MovePiece.
    [TestCase("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 3)]
    [TestCase("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 2)] // Kiwipete: castling lines
    [TestCase("r3k2r/pppq1ppp/2n1bn2/3pp3/3PP3/2N1BN2/PPPQ1PPP/R3K2R w KQkq - 0 1", 2)]   // both sides can castle
    public void Scores_MatchInLockstep_ThroughMoves(string fen, int depth)
    {
        var pos = Position.FromFen(fen);
        var board = new Board(fen);
        PieceValidMoves.GenerateValidMoves(board);
        Walk(pos, board, depth);
    }

    private static void Walk(Position pos, Board board, int depth)
    {
        // Compare the bitboard-projected score against the legacy board's score.
        Evaluation.EvaluateBoardScore(board);
        int legacy = board.Score;
        int bitboard = BitboardEval.ScoreViaLegacyGen(pos);
        if (bitboard != legacy)
        {
            var ab = BitboardEval.ToBoard(pos);
            PieceValidMoves.GenerateValidMoves(ab);
            Assert.Fail($"score mismatch at FEN {Board.Fen(false, board)}\n" +
                        $"  legacy(path)={legacy}  bitboard={bitboard}\n" +
                        DiffBoards(ab, board));
        }

        if (depth == 0) return;

        var moves = new System.Collections.Generic.List<Move>(64);
        MoveGen.GenerateLegal(pos, moves);

        foreach (Move m in moves)
        {
            // Apply the same move to a fresh legacy board clone (MovePiece infers
            // ep/castle/promotion from board state + destination; pass the chosen
            // promotion piece).
            var child = new Board(board);
            ChessPieceType promo = m.IsPromotion ? m.Promotion : ChessPieceType.Queen;
            Board.MovePiece(child, m.From, m.To, promo);
            PieceValidMoves.GenerateValidMoves(child);

            pos.MakeMove(m);
            Walk(pos, child, depth - 1);
            pos.UnmakeMove(m);
        }
    }

    // Field-by-field diff of two boards, for diagnosing eval mismatches.
    private static string DiffBoards(Board a, Board b)
    {
        var sb = new System.Text.StringBuilder();
        void Cmp(string name, object x, object y)
        {
            if (!Equals(x, y)) sb.AppendLine($"  {name}: bitboard={x} legacy={y}");
        }
        Cmp("WhoseMove", a.WhoseMove, b.WhoseMove);
        Cmp("WhiteCastled", a.WhiteCastled, b.WhiteCastled);
        Cmp("BlackCastled", a.BlackCastled, b.BlackCastled);
        Cmp("WhiteCanCastle", a.WhiteCanCastle, b.WhiteCanCastle);
        Cmp("BlackCanCastle", a.BlackCanCastle, b.BlackCanCastle);
        Cmp("EndGamePhase", a.EndGamePhase, b.EndGamePhase);
        Cmp("EnPassantPosition", a.EnPassantPosition, b.EnPassantPosition);
        Cmp("EnPassantColor", a.EnPassantColor, b.EnPassantColor);
        Cmp("HalfMoveClock", a.HalfMoveClock, b.HalfMoveClock);
        Cmp("WhiteKingPosition", a.WhiteKingPosition, b.WhiteKingPosition);
        Cmp("BlackKingPosition", a.BlackKingPosition, b.BlackKingPosition);
        Cmp("StaleMate", a.StaleMate, b.StaleMate);
        Cmp("WhiteCheck", a.WhiteCheck, b.WhiteCheck);
        Cmp("BlackCheck", a.BlackCheck, b.BlackCheck);
        for (int i = 0; i < 64; i++)
        {
            var pa = a.Squares[i].Piece;
            var pb = b.Squares[i].Piece;
            if ((pa == null) != (pb == null))
            {
                sb.AppendLine($"  sq{i}: bitboard={(pa == null ? "empty" : pa.PieceType.ToString())} legacy={(pb == null ? "empty" : pb.PieceType.ToString())}");
                continue;
            }
            // Line above guarantees both are null or neither is; say so, so the
            // compiler can see pb is non-null too.
            if (pa == null || pb == null) continue;
            if (pa.PieceType != pb.PieceType || pa.PieceColor != pb.PieceColor)
                sb.AppendLine($"  sq{i} piece: bitboard={pa.PieceColor} {pa.PieceType} legacy={pb.PieceColor} {pb.PieceType}");
            if (pa.Moved != pb.Moved)
                sb.AppendLine($"  sq{i} Moved: bitboard={pa.Moved} legacy={pb.Moved} ({pa.PieceColor} {pa.PieceType})");
            int ca = pa.ValidMoves?.Count ?? -1, cb = pb.ValidMoves?.Count ?? -1;
            if (ca != cb)
                sb.AppendLine($"  sq{i} mobility: bitboard={ca} legacy={cb} ({pa.PieceColor} {pa.PieceType})");
            if (pa.AttackedValue != pb.AttackedValue)
                sb.AppendLine($"  sq{i} AttackedValue: bitboard={pa.AttackedValue} legacy={pb.AttackedValue} ({pa.PieceColor} {pa.PieceType})");
            if (pa.DefendedValue != pb.DefendedValue)
                sb.AppendLine($"  sq{i} DefendedValue: bitboard={pa.DefendedValue} legacy={pb.DefendedValue} ({pa.PieceColor} {pa.PieceType})");
        }
        return sb.ToString();
    }
}
