using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

public sealed class GameReviewTests
{
    private const string Start = ChessGameSession.StartingFen;

    private static PlayedMove Mv(string uci, string label) => new(uci, label, 0, 0);

    /// <summary>A short game where White throws away a queen, so there is a real blunder to find.</summary>
    private static List<PlayedMove> QueenLoss() =>
    [
        Mv("e2e4", "e4"), Mv("e7e5", "e5"),
        Mv("d1h5", "Qh5"), Mv("b8c6", "Nc6"),
        Mv("h5e5", "Qxe5"), Mv("c6e5", "Nxe5"),   // White hands over the queen for a pawn
    ];

    [Test]
    public async Task EveryMoveIsReviewed_AndAttributedToTheRightSide()
    {
        var moves = QueenLoss();
        var review = await GameReviewer.ReviewAsync(Start, moves, ChessPieceColor.White, searchDeadlineMs: 200);

        Assert.Multiple(() =>
        {
            Assert.That(review.Moves, Has.Count.EqualTo(moves.Count));
            Assert.That(review.Moves.Where(m => m.IsWhite).Count(), Is.EqualTo(3));
            Assert.That(review.Moves.Count(m => m.IsHumanMove), Is.EqualTo(3), "White is the human here");
            Assert.That(review.Moves[0].MoveNumber, Is.EqualTo(1));
            Assert.That(review.Moves[2].MoveNumber, Is.EqualTo(2), "ply 3 is move 2 for White");
        });
    }

    [Test]
    public async Task LosingAQueenForAPawn_IsScoredAsABlunder()
    {
        var moves = QueenLoss();
        var review = await GameReviewer.ReviewAsync(Start, moves, ChessPieceColor.White, searchDeadlineMs: 200);

        // Ply 5 is Qxe5. Statically that looks like winning a pawn; only search sees the queen fall.
        var qxe5 = review.Moves.Single(m => m.Ply == 5);

        Assert.Multiple(() =>
        {
            Assert.That(qxe5.Loss, Is.GreaterThanOrEqualTo(GameReviewer.BlunderLoss),
                "a queen for a pawn is a blunder; a static-only review would miss it entirely");
            Assert.That(qxe5.Verdict, Is.EqualTo(MoveVerdict.Blunder));
            Assert.That(qxe5.Terms.Any(t => t.Label == "material" && t.Delta < 0), Is.True,
                "attribution should span the reply so the lost queen shows up as material");
        });
    }

    /// <summary>
    /// Tempo is +10 or -10 purely by whose turn it is, so it flips every ply. If the reviewer
    /// ever counts it again, every move silently loses 20 centipawns and sound moves get
    /// labelled inaccuracies — so pin it.
    /// </summary>
    [Test]
    public async Task TheSideToMoveBonus_IsNotChargedToEveryMove()
    {
        // A quiet, sensible opening: nothing here deserves a penalty.
        List<PlayedMove> quiet =
        [
            Mv("e2e4", "e4"), Mv("e7e5", "e5"),
            Mv("g1f3", "Nf3"), Mv("b8c6", "Nc6"),
            Mv("f1c4", "Bc4"), Mv("f8c5", "Bc5"),
        ];

        var review = await GameReviewer.ReviewAsync(Start, quiet, ChessPieceColor.White, searchDeadlineMs: 200);

        Assert.Multiple(() =>
        {
            foreach (var m in review.Moves)
            {
                Assert.That(m.Loss, Is.LessThan(GameReviewer.MistakeLoss),
                    $"{m.Label} was charged {m.Loss}; a flat tempo charge is the usual cause");
            }
            Assert.That(review.Count(MoveVerdict.Blunder), Is.Zero);
            Assert.That(review.Count(MoveVerdict.Mistake), Is.Zero);
        });
    }

    [Test]
    public async Task Explanations_AttributeToTheEngineAndNameTheTerms()
    {
        var review = await GameReviewer.ReviewAsync(Start, QueenLoss(), ChessPieceColor.White, searchDeadlineMs: 200);
        var worst = review.Moves.Where(m => m.IsHumanMove).OrderByDescending(m => m.Loss).First();

        Assert.Multiple(() =>
        {
            Assert.That(worst.Explanation, Does.StartWith("Moonforge"),
                "wording must attribute the opinion, not assert chess truth");
            Assert.That(worst.Terms, Is.Not.Empty, "a big swing should name at least one term");
            Assert.That(worst.Terms.Select(t => t.Label), Is.Unique);
            Assert.That(worst.Terms, Has.Count.LessThanOrEqualTo(3), "a sentence can't carry more than a few terms");
        });
    }

    [Test]
    public async Task SearchingTheWorstMoves_SuggestsAnAlternative()
    {
        var review = await GameReviewer.ReviewAsync(
            Start, QueenLoss(), ChessPieceColor.White, searchDeadlineMs: 300);

        Assert.That(review.Notable, Is.Not.Empty, "there is a blunder here, so something should be notable");

        var withAlternative = review.Notable.Where(m => m.PreferredLabel is not null).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(withAlternative, Is.Not.Empty, "a blundered queen should come with a better suggestion");
            foreach (var m in withAlternative)
            {
                Assert.That(m.PreferredLabel, Is.Not.Empty);
                Assert.That(m.Explanation, Does.Contain("It preferred"));
                Assert.That(m.PreferredLabel, Is.Not.EqualTo(m.Label),
                    "suggesting the move that was played is not a suggestion");
            }
        });
    }

    /// <summary>
    /// The graph is drawn from the player's side, so the same game reviewed as White and as
    /// Black must produce exactly mirrored evaluations. Get this wrong and a player with Black
    /// sees their own blunders as upward spikes.
    /// </summary>
    [Test]
    public async Task Evaluations_MirrorWhenTheOtherSideIsThePlayer()
    {
        var moves = QueenLoss();
        var asWhite = await GameReviewer.ReviewAsync(Start, moves, ChessPieceColor.White, searchDeadlineMs: 200);
        var asBlack = await GameReviewer.ReviewAsync(Start, moves, ChessPieceColor.Black, searchDeadlineMs: 200);

        Assert.Multiple(() =>
        {
            Assert.That(asBlack.StartScore, Is.EqualTo(-asWhite.StartScore));
            Assert.That(asBlack.Moves, Has.Count.EqualTo(asWhite.Moves.Count));
            for (int i = 0; i < asWhite.Moves.Count; i++)
            {
                Assert.That(asBlack.Moves[i].ScoreAfter, Is.EqualTo(-asWhite.Moves[i].ScoreAfter),
                    $"ply {i + 1} ({asWhite.Moves[i].Label}) did not mirror");
            }
            // Whose moves get judged flips with the player, but the cost of a move does not.
            Assert.That(asBlack.Moves.Select(m => m.Loss), Is.EqualTo(asWhite.Moves.Select(m => m.Loss)));
            Assert.That(asBlack.HumanMoves, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task WhiteLosingAQueen_ShowsAsADipForWhiteAndARiseForBlack()
    {
        var moves = QueenLoss();
        var asWhite = await GameReviewer.ReviewAsync(Start, moves, ChessPieceColor.White, searchDeadlineMs: 200);
        var asBlack = await GameReviewer.ReviewAsync(Start, moves, ChessPieceColor.Black, searchDeadlineMs: 200);

        int endWhite = asWhite.Moves[^1].ScoreAfter;
        int endBlack = asBlack.Moves[^1].ScoreAfter;

        Assert.Multiple(() =>
        {
            Assert.That(endWhite, Is.LessThan(-200), "White gave up a queen; the line should end well below zero");
            Assert.That(endBlack, Is.GreaterThan(200), "and the same position should read as winning for Black");
        });
    }

    [Test]
    public async Task AnEmptyGame_ReviewsToNothingRatherThanThrowing()
    {
        var review = await GameReviewer.ReviewAsync(Start, [], ChessPieceColor.White);

        Assert.Multiple(() =>
        {
            Assert.That(review.Moves, Is.Empty);
            Assert.That(review.Notable, Is.Empty);
            Assert.That(review.HumanMoves, Is.Zero);
        });
    }

    [Test]
    public void Cancelling_StopsTheReview()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            GameReviewer.ReviewAsync(Start, QueenLoss(), ChessPieceColor.White, token: cts.Token));
    }
}
