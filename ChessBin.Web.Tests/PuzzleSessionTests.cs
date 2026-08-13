using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

public sealed class PuzzleSessionTests
{
    private PuzzleSession _session = null!;

    [SetUp]
    public void SetUp() => _session = new PuzzleSession();

    /// <summary>
    /// A real puzzle from the shipped data, so these tests exercise the same records the
    /// page will actually load.
    /// </summary>
    private static PuzzleRecord FirstShippedPuzzle()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
            dir = dir.Parent;

        string path = Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot", "puzzles", "shard-000.json");
        return PuzzleData.ParseShard(File.ReadAllText(path))[0];
    }

    /// <summary>Two rooks on the first rank: both Ra8 and Rb8 are mate, so the line has more than one answer.</summary>
    private static PuzzleRecord TwoMatesPuzzle() => new(
        Id: "test-two-mates",
        Fen: "6k1/5ppp/8/8/8/8/8/RR5K w - - 0 1",
        LastMove: "g2g1",
        Solution: ["a1a8"],
        Rating: 1200,
        Themes: ["mateIn1", "backRankMate"],
        Url: "https://example.invalid/");

    private async Task ClickMoveAsync(string uci)
    {
        await _session.ClickSquareAsync(uci[0] - 'a', 8 - (uci[1] - '0'));
        await _session.ClickSquareAsync(uci[2] - 'a', 8 - (uci[3] - '0'));
    }

    [Test]
    public void Load_PutsTheSolverOnMoveWithNoProgress()
    {
        var puzzle = FirstShippedPuzzle();
        _session.Load(puzzle);

        Assert.Multiple(() =>
        {
            Assert.That(_session.IsLoaded, Is.True);
            Assert.That(_session.Outcome, Is.EqualTo(PuzzleOutcome.Solving));
            Assert.That(_session.IsSolverTurn, Is.True);
            Assert.That(_session.SolverMovesMade, Is.Zero);
            Assert.That(_session.SolverMovesTotal, Is.EqualTo(puzzle.SolverMoveCount));
            Assert.That(_session.Fen, Is.EqualTo(puzzle.Fen));
            Assert.That(_session.GetDisplaySquares(), Has.Count.EqualTo(64));
            // The board orients to whoever has to solve it.
            Assert.That(_session.WhiteAtBottom, Is.EqualTo(_session.SolverColor == ChessPieceColor.White));
        });
    }

    [Test]
    public async Task PlayingTheWholeSolution_SolvesItCleanly()
    {
        var puzzle = FirstShippedPuzzle();
        _session.Load(puzzle);

        for (int ply = 0; ply < puzzle.Solution.Length; ply += 2)
            await ClickMoveAsync(puzzle.Solution[ply]);

        Assert.Multiple(() =>
        {
            Assert.That(_session.Outcome, Is.EqualTo(PuzzleOutcome.Solved));
            Assert.That(_session.Mistakes, Is.Zero);
            Assert.That(_session.HintUsed, Is.False);
            Assert.That(_session.IsClean, Is.True);
            Assert.That(_session.SolverMovesMade, Is.EqualTo(_session.SolverMovesTotal));
            Assert.That(_session.IsSolverTurn, Is.False, "a solved puzzle must stop accepting moves");
            Assert.That(_session.Line, Has.Count.EqualTo(puzzle.Solution.Length));
        });
    }

    [Test]
    public async Task AWrongMove_CountsAMistakeAndLeavesThePositionUntouched()
    {
        var puzzle = FirstShippedPuzzle();
        _session.Load(puzzle);

        // Find any legal move that isn't the solution's first move.
        string expected = puzzle.Solution[0];
        string? wrong = null;
        var engine = new Engine(puzzle.Fen);
        engine.GenerateValidMoves();
        for (byte column = 0; column < 8 && wrong is null; column++)
        {
            for (byte row = 0; row < 8 && wrong is null; row++)
            {
                if (engine.GetPieceTypeAt(column, row) == ChessPieceType.None) continue;
                if (engine.GetPieceColorAt(column, row) != _session.SolverColor) continue;

                foreach (byte[] target in engine.GetValidMoves(column, row) ?? [])
                {
                    if (!engine.IsValidMove(column, row, target[0], target[1])) continue;
                    string candidate = $"{(char)('a' + column)}{8 - row}{(char)('a' + target[0])}{8 - target[1]}";
                    if (candidate == expected[..4]) continue;
                    wrong = candidate;
                    break;
                }
            }
        }

        Assert.That(wrong, Is.Not.Null, "the position should offer some other legal move");

        await ClickMoveAsync(wrong!);

        Assert.Multiple(() =>
        {
            Assert.That(_session.Mistakes, Is.EqualTo(1));
            Assert.That(_session.Outcome, Is.EqualTo(PuzzleOutcome.Solving));
            Assert.That(_session.Fen, Is.EqualTo(puzzle.Fen), "a rejected move must not change the board");
            Assert.That(_session.IsClean, Is.False);
        });
    }

    [Test]
    public async Task AfterAWrongMove_TheRealSolutionStillWorks()
    {
        var puzzle = TwoMatesPuzzle();
        _session.Load(puzzle);

        await ClickMoveAsync("h1g2");                 // legal, but not a mate
        Assert.That(_session.Mistakes, Is.EqualTo(1));

        await ClickMoveAsync("a1a8");                 // the recorded solution

        Assert.Multiple(() =>
        {
            Assert.That(_session.Outcome, Is.EqualTo(PuzzleOutcome.Solved));
            Assert.That(_session.Mistakes, Is.EqualTo(1), "the earlier mistake should still count");
            Assert.That(_session.IsClean, Is.False);
            Assert.That(_session.EndedInMate, Is.True);
        });
    }

    [Test]
    public async Task ADifferentMateIsAccepted_NotPenalised()
    {
        var puzzle = TwoMatesPuzzle();
        _session.Load(puzzle);

        await ClickMoveAsync("b1b8");                 // mate, but not the move on file

        Assert.Multiple(() =>
        {
            Assert.That(_session.Outcome, Is.EqualTo(PuzzleOutcome.Solved));
            Assert.That(_session.Mistakes, Is.Zero, "an alternative mate is a solution, not a mistake");
            Assert.That(_session.EndedInMate, Is.True);
            Assert.That(_session.Status, Does.Contain("mate"));
        });
    }

    [Test]
    public void Hint_HighlightsThePieceAndCostsTheCleanSolve()
    {
        var puzzle = FirstShippedPuzzle();
        _session.Load(puzzle);
        _session.ShowHint();

        int expectedFrom = (puzzle.Solution[0][0] - 'a') + (8 - (puzzle.Solution[0][1] - '0')) * 8;

        Assert.Multiple(() =>
        {
            Assert.That(_session.HintUsed, Is.True);
            Assert.That(_session.HintSquare, Is.EqualTo(expectedFrom));
            Assert.That(_session.IsClean, Is.False, "a hinted solve is not a clean one");
        });
    }

    [Test]
    public async Task GivingUp_PlaysOutTheLineAndMarksItRevealed()
    {
        var puzzle = FirstShippedPuzzle();
        _session.Load(puzzle);

        await _session.RevealAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_session.Outcome, Is.EqualTo(PuzzleOutcome.Revealed));
            Assert.That(_session.SolverMovesMade, Is.EqualTo(_session.SolverMovesTotal));
            Assert.That(_session.Line, Has.Count.EqualTo(puzzle.Solution.Length));
            Assert.That(_session.IsClean, Is.False, "a revealed puzzle was never solved");
            Assert.That(_session.IsSolverTurn, Is.False);
        });
    }

    [Test]
    public async Task TheEvaluationSwing_FavoursTheSolver()
    {
        // A mate ends the game, so pick a shipped puzzle that wins material instead.
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
            dir = dir.Parent;
        var shard = PuzzleData.ParseShard(File.ReadAllText(
            Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot", "puzzles", "shard-000.json")));

        var puzzle = shard.First(p => p.Themes.Contains("crushing") || p.Themes.Contains("advantage"));
        _session.Load(puzzle);

        await _session.RevealAsync();

        // Solving a winning tactic should not leave the solver worse off than they started.
        Assert.That(_session.EvaluationSwing, Is.GreaterThanOrEqualTo(0),
            $"puzzle {puzzle.Id} ({string.Join(' ', puzzle.Themes)}) swung {_session.EvaluationSwing} against the solver");
    }
}
