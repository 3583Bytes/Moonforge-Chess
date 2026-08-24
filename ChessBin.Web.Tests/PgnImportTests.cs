using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

/// <summary>
/// The parser exists so people can review games from elsewhere, so the fixtures are the
/// shapes Lichess and Chess.com actually export — clocks, evals, repeated black move
/// numbers and all.
/// </summary>
public sealed class PgnImportTests
{
    [Test]
    public void APlainMainline_Reads()
    {
        const string pgn = """
            [Event "Casual game"]
            [White "alice"]
            [Black "bob"]
            [Result "1-0"]

            1. e4 e5 2. Nf3 Nc6 3. Bc4 Bc5 4. c3 Nf6 5. d4 exd4 6. cxd4 Bb4+ 7. Nc3 1-0
            """;

        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves, Has.Count.EqualTo(13));
            Assert.That(r.Game.White, Is.EqualTo("alice"));
            Assert.That(r.Game.Black, Is.EqualTo("bob"));
            Assert.That(r.Game.Result, Is.EqualTo("1-0"));
            Assert.That(r.Game.Moves[0].Uci, Is.EqualTo("e2e4"));
            Assert.That(r.Game.Moves[0].Label, Is.EqualTo("e4"));
            Assert.That(r.Game.Moves[9].Label, Is.EqualTo("exd4"), "captures keep their notation");
            Assert.That(r.Game.Moves[11].Label, Is.EqualTo("Bb4"), "the check mark is stripped from the label");
        });
    }

    [Test]
    public void LichessExport_WithClocksEvalsAndRepeatedMoveNumbers_Reads()
    {
        const string pgn = """
            [Event "Rated Blitz game"]
            [Site "https://lichess.org/abcd1234"]
            [Date "2026.08.24"]
            [White "carol"]
            [Black "dave"]
            [Result "0-1"]
            [TimeControl "300+0"]
            [ECO "B20"]

            1. e4 { [%eval 0.17] [%clk 0:05:00] } 1... c5 { [%eval 0.24] [%clk 0:05:00] }
            2. Nf3 { [%eval 0.2] [%clk 0:04:57] } 2... d6 { [%clk 0:04:58] }
            3. d4 cxd4 4. Nxd4 Nf6 0-1
            """;

        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves, Has.Count.EqualTo(8));
            Assert.That(r.Game.Moves.Select(m => m.Label),
                Is.EqualTo(new[] { "e4", "c5", "Nf3", "d6", "d4", "cxd4", "Nxd4", "Nf6" }));
        });
    }

    [Test]
    public void ChessComExport_WithInlineClockComments_Reads()
    {
        const string pgn = """
            [Event "Live Chess"]
            [Site "Chess.com"]
            [White "erin"]
            [Black "frank"]
            [Result "1/2-1/2"]

            1. d4 {[%clk 0:02:59.9]} 1... Nf6 {[%clk 0:02:58.8]} 2. c4 {[%clk 0:02:57]} 2... e6 1/2-1/2
            """;

        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves.Select(m => m.Label), Is.EqualTo(new[] { "d4", "Nf6", "c4", "e6" }));
            Assert.That(r.Game.Result, Is.EqualTo("1/2-1/2"));
        });
    }

    [Test]
    public void VariationsAndNags_AreSkippedNotPlayed()
    {
        const string pgn = """
            [Result "*"]

            1. e4 e5 (1... c5 2. Nf3 d6 (2... Nc6 3. d4)) $1 2. Nf3 $14 Nc6 *
            """;

        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves.Select(m => m.Label), Is.EqualTo(new[] { "e4", "e5", "Nf3", "Nc6" }),
                "nested variations must be dropped whole, not partly played");
        });
    }

    [Test]
    public void Castling_BothSidesAndBothNotations_Reads()
    {
        const string pgn = "1. e4 e5 2. Nf3 Nf6 3. Bc4 Bc5 4. O-O 0-0 *";
        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves[6].Uci, Is.EqualTo("e1g1"), "White castles short");
            Assert.That(r.Game.Moves[7].Uci, Is.EqualTo("e8g8"), "and zero-notation castling still reads");
        });
    }

    [Test]
    public void Disambiguation_ByFileAndByRank_PicksTheRightPiece()
    {
        // Knights on b1 and f3 can both reach d2; "Nbd2" names the one on b1.
        const string pgn = "1. d4 d5 2. Nf3 Nf6 3. Nbd2 *";
        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves[^1].Uci, Is.EqualTo("b1d2"));
        });
    }

    [Test]
    public void Promotion_ReadsThePieceAndEncodesItInUci()
    {
        // Black is helpless while a white pawn walks in and promotes to a rook.
        const string pgn = """
            [SetUp "1"]
            [FEN "7k/4P3/8/8/8/8/8/7K w - - 0 1"]

            1. e8=R Kg7 *
            """;

        var r = PgnImport.Parse(pgn);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.StartsFromStandardPosition, Is.False, "the FEN tag should be honoured");
            Assert.That(r.Game.Moves[0].Uci, Is.EqualTo("e7e8r"), "an underpromotion must survive the round trip");
            Assert.That(r.Game.Moves[0].Label, Is.EqualTo("e8=R"));
        });
    }

    [Test]
    public async Task AnImportedGame_FeedsStraightIntoTheReviewer()
    {
        const string pgn = "1. e4 e5 2. Nf3 Nc6 3. Bc4 Bc5 4. c3 Nf6 5. d4 exd4 6. cxd4 Bb4+ 7. Nc3 *";
        var r = PgnImport.Parse(pgn);
        Assert.That(r.Success, Is.True, r.Error);

        var review = await GameReviewer.ReviewAsync(
            r.Game!.StartFen, r.Game.Moves, ChessPieceColor.White, searchDeadlineMs: 200);

        Assert.Multiple(() =>
        {
            Assert.That(review.Moves, Has.Count.EqualTo(r.Game.Moves.Count));
            Assert.That(review.HumanMoves, Is.EqualTo(7));
            Assert.That(review.Moves.All(m => m.Explanation.Length > 0), Is.True);
        });
    }

    [Test]
    public void EnPassant_Reads()
    {
        // 3...c5 invites 4. dxc6 en passant.
        const string pgn = "1. e4 Nf6 2. e5 Nd5 3. d4 c5 4. dxc5 *";
        var plain = PgnImport.Parse(pgn);
        Assert.That(plain.Success, Is.True, plain.Error);

        // and the real thing: black pawn steps past a white pawn on the fifth rank
        const string ep = "1. e4 Nf6 2. e5 d5 3. exd6 *";
        var r = PgnImport.Parse(ep);

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True, r.Error);
            Assert.That(r.Game!.Moves[^1].Uci, Is.EqualTo("e5d6"), "the capture lands on the empty square behind the pawn");
        });
    }

    [Test]
    public void AnIllegalCastle_IsRefusedRatherThanApplied()
    {
        // Nothing has moved, so O-O is impossible — and Engine.MovePiece would happily do it.
        var r = PgnImport.Parse("1. O-O *");

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.False, "the parser must gate castling, not trust MovePiece");
            Assert.That(r.Error, Does.Contain("O-O"));
        });
    }

    [Test]
    public void GarbageAndEmptyInput_FailWithSomethingReadable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PgnImport.Parse("").Error, Does.Contain("Paste a game"));
            Assert.That(PgnImport.Parse("   ").Success, Is.False);
            Assert.That(PgnImport.Parse("[White \"a\"]\n[Black \"b\"]").Error, Does.Contain("No moves"));

            var illegal = PgnImport.Parse("1. e4 e5 2. Qh9 *");
            Assert.That(illegal.Success, Is.False);
            Assert.That(illegal.Error, Does.Contain("Qh9").Or.Contain("move 3"));
        });
    }

    [Test]
    public void AnIllegalMoveMidGame_SaysWhichMoveFailed()
    {
        // Nf3 is fine; Nf3 again from nowhere is not.
        var r = PgnImport.Parse("1. e4 e5 2. Nf3 Nc6 3. Nf3 *");

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.False);
            Assert.That(r.Error, Does.Contain("5"), "the message should point at the offending move number");
        });
    }
}
