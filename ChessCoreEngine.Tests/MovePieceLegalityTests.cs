using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

/// <summary>
/// Engine.MovePiece is the public way to apply a move, and its bool return is what every
/// caller treats as "was that legal". It used to apply the move regardless and return true,
/// checking only whether the mover had exposed their own king.
/// </summary>
public sealed class MovePieceLegalityTests
{
    private static Engine At(string fen)
    {
        var engine = new Engine(fen);
        engine.GenerateValidMoves();
        return engine;
    }

    private const string Start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [Test]
    public void APieceCannotMoveThroughItsOwnPawn()
    {
        // Bc4 is blocked by White's own pawn on d5, so Bxf7 is impossible.
        var engine = At("r1bqkb1r/ppp2ppp/5n2/3Pp1N1/2Bn4/8/PPPP1PPP/RNBQK2R w KQkq - 1 6");
        string before = engine.FEN;

        Assert.Multiple(() =>
        {
            Assert.That(engine.MovePiece(2, 4, 5, 1), Is.False, "c4 to f7 passes through the pawn on d5");
            Assert.That(engine.FEN, Is.EqualTo(before), "a rejected move must leave the position alone");
        });
    }

    [Test]
    public void CastlingIsRefusedWhenThePathIsOccupied()
    {
        var engine = At(Start);
        string before = engine.FEN;

        Assert.Multiple(() =>
        {
            Assert.That(engine.MovePiece(4, 7, 6, 7), Is.False, "nothing has moved yet, so O-O is impossible");
            Assert.That(engine.FEN, Is.EqualTo(before));
        });
    }

    [Test]
    public void AnEmptySourceSquare_ReturnsFalseRatherThanThrowing()
    {
        var engine = At(Start);
        Assert.That(engine.MovePiece(0, 4, 0, 3), Is.False, "a4 is empty in the starting position");
    }

    [Test]
    public void MovingTheOtherSidesPiece_IsRefused()
    {
        var engine = At(Start);   // White to move
        string before = engine.FEN;

        Assert.Multiple(() =>
        {
            Assert.That(engine.MovePiece(4, 1, 4, 3), Is.False, "black's e-pawn cannot move on White's turn");
            Assert.That(engine.FEN, Is.EqualTo(before));
        });
    }

    [Test]
    public void MovingAPinnedPiece_IsStillRefused()
    {
        // Black knight on e6 is pinned to the king on e8 by the rook on e1.
        var engine = At("4k3/8/4n3/8/8/8/8/4R2K b - - 0 1");
        Assert.That(engine.MovePiece(4, 2, 3, 4), Is.False, "the self-check unwind must still work");
    }

    // ── the gates must not reject anything legal ────────────────────────────────

    [Test]
    public void OrdinaryMovesCapturesAndPromotionsStillApply()
    {
        Assert.Multiple(() =>
        {
            var pawn = At(Start);
            Assert.That(pawn.MovePiece(4, 6, 4, 4), Is.True, "e2-e4");

            var capture = At("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2");
            Assert.That(capture.MovePiece(4, 4, 3, 3), Is.True, "exd5");

            var promote = At("7k/4P3/8/8/8/8/8/7K w - - 0 1");
            promote.PromoteToPieceType = ChessPieceType.Rook;
            Assert.That(promote.MovePiece(4, 1, 4, 0), Is.True, "e8=R");
        });
    }

    [Test]
    public void CastlingBothSidesStillApplies()
    {
        Assert.Multiple(() =>
        {
            var shortSide = At("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 0 6");
            Assert.That(shortSide.MovePiece(4, 7, 6, 7), Is.True, "O-O");

            var longSide = At("r3k2r/pppq1ppp/2npbn2/2b1p3/2B1P3/2NPBN2/PPPQ1PPP/R3K2R w KQkq - 0 9");
            Assert.That(longSide.MovePiece(4, 7, 2, 7), Is.True, "O-O-O");
        });
    }

    [Test]
    public void EnPassantStillApplies()
    {
        // White pawn on e5, black has just played d7-d5, so exd6 is available.
        var engine = At("rnbqkbnr/ppp1pppp/8/3pP3/8/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 3");
        Assert.That(engine.MovePiece(4, 3, 3, 2), Is.True, "exd6 en passant");
    }

    /// <summary>
    /// The search picks moves with the bitboard core, while MovePiece now gates on the mailbox
    /// generator. The two are separate implementations, so a disagreement would show up as the
    /// engine's own move being refused — which the web app turns into an exception mid-game.
    /// </summary>
    [Test]
    public void TheEnginesOwnMovesAreNeverRefused()
    {
        int applied = 0;

        for (int game = 0; game < 2; game++)
        {
            var engine = new Engine { GameDifficulty = Engine.Difficulty.Easy, SearchDeadlineMs = 80 };
            engine.GenerateValidMoves();

            for (int ply = 0; ply < 40 && !engine.IsGameOver(); ply++)
            {
                EngineSearchResult result = engine.SearchBestMove();
                if (!result.HasMove || result.BestMove.Length < 4) break;

                engine.PromoteToPieceType = result.BestMove.Length == 5
                    ? result.BestMove[4] switch
                    {
                        'r' => ChessPieceType.Rook,
                        'b' => ChessPieceType.Bishop,
                        'n' => ChessPieceType.Knight,
                        _ => ChessPieceType.Queen,
                    }
                    : ChessPieceType.Queen;

                Assert.That(engine.MovePieceAN(result.BestMove[..4]), Is.True,
                    $"the engine chose {result.BestMove} but MovePiece refused it in {engine.FEN}");
                applied++;
            }
        }

        Assert.That(applied, Is.GreaterThan(40), "the probe should have played a meaningful number of moves");
    }

    [Test]
    public void AFullLegalGameStillReplays()
    {
        var engine = At(Start);
        (byte fc, byte fr, byte tc, byte tr)[] game =
        [
            (4, 6, 4, 4), (4, 1, 4, 3),   // e4 e5
            (6, 7, 5, 5), (6, 0, 5, 2),   // Nf3 Nf6   (clears g8 so Black can castle)
            (5, 7, 2, 4), (5, 0, 2, 3),   // Bc4 Bc5
            (4, 7, 6, 7), (4, 0, 6, 0),   // O-O O-O
        ];

        Assert.Multiple(() =>
        {
            for (int i = 0; i < game.Length; i++)
            {
                var (fc, fr, tc, tr) = game[i];
                Assert.That(engine.MovePiece(fc, fr, tc, tr), Is.True, $"move {i + 1} should be legal");
            }
        });
    }
}
