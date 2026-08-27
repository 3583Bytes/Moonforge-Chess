using ChessEngine.Engine;

namespace ChessBin.Web;

public enum MoveVerdict
{
    /// <summary>The engine would have played this too.</summary>
    Best,
    Solid,
    Inaccuracy,
    Mistake,
    Blunder,
}

/// <summary>One named evaluation term and how far it moved, in centipawns, from the mover's side.</summary>
public sealed record TermDelta(string Label, int Delta);

public sealed record ReviewedMove(
    int Ply,
    string Label,
    string Uci,
    ChessPieceColor Mover,
    int Loss,
    int ScoreAfter,
    double Accuracy,
    double WinGain,
    string FenAfter,
    MoveVerdict Verdict,
    IReadOnlyList<TermDelta> Terms,
    string Explanation)
{
    /// <summary>What the engine would have played, when it differs from what was played.</summary>
    public string? PreferredLabel { get; init; }
    public bool IsHumanMove { get; init; }
    public int MoveNumber => (Ply + 1) / 2;
    public string Reference => IsWhite ? $"{MoveNumber}. {Label}" : $"{MoveNumber}… {Label}";

    /// <summary>The evaluation after this move, from the player's side.</summary>
    public int Score() => ScoreAfter;
    public bool IsWhite => Mover == ChessPieceColor.White;
}

public sealed record GameReviewResult(
    IReadOnlyList<ReviewedMove> Moves,
    IReadOnlyList<ReviewedMove> Notable,
    ChessPieceColor HumanColor,
    int StartScore)
{
    public int Count(MoveVerdict v) => Moves.Count(m => m.IsHumanMove && m.Verdict == v);
    public int HumanMoves => Moves.Count(m => m.IsHumanMove);

    /// <summary>How accurately the player played, 0–100. Zero moves means no score.</summary>
    public int Accuracy
    {
        get
        {
            var mine = Moves.Where(m => m.IsHumanMove).ToArray();
            return mine.Length == 0 ? 0 : (int)Math.Round(mine.Average(m => m.Accuracy));
        }
    }

    /// <summary>Moves the engine would have played itself.</summary>
    public int BestMoves => Count(MoveVerdict.Best);

    /// <summary>
    /// The move worth being pleased about: the engine's own choice that gained the most, so
    /// there is something to celebrate and not only mistakes to apologise for.
    /// </summary>
    public ReviewedMove? BestMoment
    {
        get
        {
            var mine = Moves.Where(m => m.IsHumanMove && m.WinGain >= MinCelebratedGain).ToArray();
            if (mine.Length == 0) return null;

            var agreed = mine.Where(m => m.Verdict == MoveVerdict.Best).ToArray();
            return (agreed.Length > 0 ? agreed : mine).MaxBy(m => m.WinGain);
        }
    }

    /// <summary>Below this a "best moment" is noise, and praising noise cheapens the praise.</summary>
    private const double MinCelebratedGain = 3.0;
}

/// <summary>
/// Walks a finished game, says what each move cost, and attributes the change to the engine's
/// own named evaluation terms.
/// <para>
/// The cost comes from search, not from comparing static evaluations either side of a move:
/// statically, grabbing a defended pawn with the queen looks like winning a pawn, because the
/// refutation is a ply away. Only a search sees that.
/// </para>
/// <para>
/// Every position is searched exactly once. <see cref="EngineSearchInfo.Score"/> is from the
/// side to move, so consecutive scores face opposite ways and a move's cost is simply
/// <c>S(before) + S(after)</c>.
/// </para>
/// </summary>
public static class GameReviewer
{
    public const int InaccuracyLoss = 50;
    public const int MistakeLoss = 120;
    public const int BlunderLoss = 300;

    /// <summary>Terms smaller than this are noise, not explanation.</summary>
    private const int MinTermCp = 12;

    /// <summary>How many terms a sentence can carry before it stops being readable.</summary>
    private const int MaxTermsQuoted = 3;

    /// <summary>Mate scores are enormous; clamp so "missed a mate" stays a number, not an overflow.</summary>
    private const int ScoreClamp = 3_000;

    /// <summary>How far down the engine's expected line the attribution looks.</summary>
    private const int MaxPvPlies = 6;

    /// <summary>
    /// Centipawns to a winning percentage. This is the logistic Lichess uses, kept rather than
    /// invented so the number means the same thing as the one players already know.
    /// </summary>
    public static double WinPercent(int centipawns) =>
        50 + 50 * (2 / (1 + Math.Exp(-0.00368208 * centipawns)) - 1);

    /// <summary>
    /// How accurate a single move was, from the winning percentage it gave up. Also the
    /// standard shape: a move that loses nothing scores 100, and the penalty grows sharply.
    /// </summary>
    public static double MoveAccuracy(double winPercentBefore, double winPercentAfter) =>
        Math.Clamp(103.1668 * Math.Exp(-0.04354 * (winPercentBefore - winPercentAfter)) - 3.1669, 0, 100);

    /// <summary>Default number of the player's worst moments to surface.</summary>
    public const int DefaultNotable = 5;

    /// <summary>
    /// Tempo is +10 or -10 purely by whose turn it is, so it flips every ply. Excluded from the
    /// attribution so it doesn't charge every move a flat 20 centipawns.
    /// </summary>
    private static int PositionScore(EvaluationBreakdown b) => b.Total - b.Tempo;

    public static async Task<GameReviewResult> ReviewAsync(
        string initialFen,
        IReadOnlyList<PlayedMove> moves,
        ChessPieceColor humanColor,
        long searchDeadlineMs = 250,
        int notableCount = DefaultNotable,
        Action<int, int>? progress = null,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialFen);
        ArgumentNullException.ThrowIfNull(moves);

        if (moves.Count == 0)
            return new GameReviewResult([], [], humanColor, 0);

        var engine = new Engine(initialFen)
        {
            GameDifficulty = Engine.Difficulty.Medium,
            SearchDeadlineMs = searchDeadlineMs,
        };
        engine.GenerateValidMoves();

        var scores = new List<int>(moves.Count + 1);
        var best = new List<string?>(moves.Count + 1);
        var pvs = new List<string>(moves.Count + 1);
        var statics = new List<EvaluationBreakdown> { engine.GetEvaluationBreakdown() };
        var movers = new List<ChessPieceColor>(moves.Count);
        var fens = new List<string>(moves.Count);
        int played = 0;

        // One search per position: the score after move i is also the score before move i+1.
        for (int k = 0; k <= moves.Count; k++)
        {
            token.ThrowIfCancellationRequested();

            EngineSearchResult result = await Task.Run(() => engine.SearchBestMove(token, _ => { }), token);
            scores.Add(Math.Clamp(result.Info.Score, -ScoreClamp, ScoreClamp));
            best.Add(result.HasMove ? result.BestMove : null);
            pvs.Add(result.Info.PrincipalVariation ?? string.Empty);

            progress?.Invoke(k + 1, moves.Count + 1);
            if (k == moves.Count) break;

            movers.Add(engine.WhoseMove);
            if (!PuzzleData.TryApplyUci(engine, moves[k].Uci)) break;   // unreplayable game stops here
            statics.Add(engine.GetEvaluationBreakdown());
            fens.Add(engine.FEN);
            played++;
        }

        // Info.Score is from the side to move. Re-express every position from the player's
        // side so a dip in the graph always means the player is worse off — otherwise a
        // player with Black would see their own blunders as upward spikes.
        int humanSign = humanColor == ChessPieceColor.White ? 1 : -1;
        int ScoreForHuman(int k)
        {
            ChessPieceColor toMove = k < movers.Count
                ? movers[k]
                : (movers[^1] == ChessPieceColor.White ? ChessPieceColor.Black : ChessPieceColor.White);
            int whitePov = scores[k] * (toMove == ChessPieceColor.White ? 1 : -1);
            return whitePov * humanSign;
        }

        var reviewed = new List<ReviewedMove>(played);
        for (int i = 0; i < played && i + 1 < scores.Count; i++)
        {
            ChessPieceColor mover = movers[i];
            int sign = mover == ChessPieceColor.White ? 1 : -1;
            int loss = scores[i] + scores[i + 1];

            bool matchedEngine = best[i] is { Length: >= 4 } b
                                 && moves[i].Uci.Length >= 4
                                 && b[..4].Equals(moves[i].Uci[..4], StringComparison.OrdinalIgnoreCase);

            MoveVerdict verdict = matchedEngine ? MoveVerdict.Best : VerdictFor(loss);

            double winBefore = WinPercent(ScoreForHuman(i));
            double winAfter = WinPercent(ScoreForHuman(i + 1));
            bool mine = mover == humanColor;
            // Accuracy only means anything for the player; the engine is not being marked.
            double accuracy = mine ? MoveAccuracy(winBefore, winAfter) : 0;
            double gain = mine ? winAfter - winBefore : 0;

            // Only moves that actually cost something get an attribution and an alternative.
            // Second-guessing a sound move is noise, and the attribution follows the engine's own
            // expected continuation rather than whatever happened next in the game — attributing
            // across the game's next ply can contradict the search outright, showing material won
            // on the board for a move the search scores as losing.
            IReadOnlyList<TermDelta> terms = [];
            string? preferred = null;
            if (verdict >= MoveVerdict.Inaccuracy)
            {
                (terms, preferred) = await ExplainAsync(
                    initialFen, moves, i, pvs[i + 1], matchedEngine ? null : best[i],
                    statics[i], sign, searchDeadlineMs, token);
            }

            reviewed.Add(new ReviewedMove(
                Ply: i + 1,
                Label: moves[i].Label,
                Uci: moves[i].Uci,
                Mover: mover,
                Loss: Math.Max(0, loss),
                ScoreAfter: ScoreForHuman(i + 1),
                Accuracy: accuracy,
                WinGain: gain,
                FenAfter: i < fens.Count ? fens[i] : string.Empty,
                Verdict: verdict,
                Terms: terms,
                Explanation: Describe(verdict, loss, terms, preferred))
            {
                PreferredLabel = preferred,
                IsHumanMove = mover == humanColor,
            });
        }

        var notable = reviewed
            .Where(m => m.IsHumanMove && m.Verdict >= MoveVerdict.Inaccuracy)
            .OrderByDescending(m => m.Loss)
            .Take(notableCount)
            .ToArray();

        return new GameReviewResult(reviewed, notable, humanColor,
            movers.Count > 0 ? ScoreForHuman(0) : 0);
    }

    /// <summary>
    /// For one costly move: follows the engine's expected continuation to attribute the change,
    /// and renders its preferred alternative in real notation rather than raw coordinates.
    /// </summary>
    private static async Task<(IReadOnlyList<TermDelta> Terms, string? Preferred)> ExplainAsync(
        string initialFen,
        IReadOnlyList<PlayedMove> moves,
        int index,
        string principalVariation,
        string? preferredUci,
        EvaluationBreakdown before,
        int sign,
        long searchDeadlineMs,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Task.Yield();

        // The engine's expected line, starting from the position the played move produced.
        var line = Replay(initialFen, moves, index + 1, searchDeadlineMs);
        IReadOnlyList<TermDelta> terms = [];
        if (line is not null)
        {
            foreach (string mv in principalVariation.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(MaxPvPlies))
            {
                if (!PuzzleData.TryApplyUci(line, mv)) break;
            }
            terms = TermsFor(before, line.GetEvaluationBreakdown(), sign);
        }

        // The alternative, played on a fresh replay purely to read its algebraic label.
        string? preferred = null;
        if (preferredUci is { Length: >= 4 })
        {
            var alt = Replay(initialFen, moves, index, searchDeadlineMs);
            if (alt is not null && PuzzleData.TryApplyUci(alt, preferredUci))
                preferred = string.IsNullOrWhiteSpace(alt.LastMove.PgnMove) ? preferredUci : alt.LastMove.PgnMove;
        }

        return (terms, preferred);
    }

    private static Engine? Replay(string initialFen, IReadOnlyList<PlayedMove> moves, int plyCount, long deadline)
    {
        var engine = new Engine(initialFen) { SearchDeadlineMs = deadline };
        engine.GenerateValidMoves();
        for (int k = 0; k < plyCount; k++)
        {
            if (!PuzzleData.TryApplyUci(engine, moves[k].Uci)) return null;
        }
        return engine;
    }

    private static MoveVerdict VerdictFor(int loss) => loss switch
    {
        >= BlunderLoss => MoveVerdict.Blunder,
        >= MistakeLoss => MoveVerdict.Mistake,
        >= InaccuracyLoss => MoveVerdict.Inaccuracy,
        _ => MoveVerdict.Solid,
    };

    private static IReadOnlyList<TermDelta> TermsFor(EvaluationBreakdown a, EvaluationBreakdown b, int sign)
    {
        (string Label, int Delta)[] all =
        [
            ("material", (b.Material - a.Material) * sign),
            ("piece placement", (b.PieceSquareTables - a.PieceSquareTables) * sign),
            ("piece mobility", (b.Mobility - a.Mobility) * sign),
            ("attack and defence", (b.AttackDefense - a.AttackDefense) * sign),
            ("pawn structure", (b.PawnStructure - a.PawnStructure) * sign),
            ("king safety", (b.KingSafety - a.KingSafety) * sign),
            ("minor pieces", (b.MinorPieceAdjustments - a.MinorPieceAdjustments) * sign),
            ("queen development", (b.QueenDevelopment - a.QueenDevelopment) * sign),
            ("castling", (b.Castling - a.Castling) * sign),
            ("checks", (b.Check - a.Check) * sign),
        ];

        return all
            .Where(t => Math.Abs(t.Delta) >= MinTermCp)
            .OrderByDescending(t => Math.Abs(t.Delta))
            .Take(MaxTermsQuoted)
            .Select(t => new TermDelta(t.Label, t.Delta))
            .ToArray();
    }

    /// <summary>
    /// Attributes rather than asserts. A classical evaluation's terms are the engine's opinion,
    /// not chess truth, so the wording says whose opinion it is — and names the window the
    /// numbers cover, since the cost and the attribution measure different spans.
    /// </summary>
    private static string Describe(
        MoveVerdict verdict, int loss, IReadOnlyList<TermDelta> terms, string? preferred)
    {
        if (verdict == MoveVerdict.Best)
            return "Moonforge would have played this too.";

        string head = loss >= InaccuracyLoss
            ? $"Moonforge scores this as costing {Cost(loss)}"
            : "Moonforge scores this as holding the balance";

        string body = terms.Count == 0
            ? $"{head}."
            : $"{head}. Along the line it expects: " +
              $"{string.Join(", ", terms.Select(t => $"{t.Label} {Cp(t.Delta)}"))}.";

        return preferred is null ? body : $"{body} It preferred {preferred}.";
    }

    /// <summary>A cost is signed by being a cost, so it prints unsigned.</summary>
    private static string Cost(int centipawns) => $"{Math.Abs(centipawns) / 100d:0.00}";

    private static string Cp(int centipawns) => centipawns switch
    {
        > 0 => $"+{centipawns / 100d:0.00}",
        < 0 => $"{centipawns / 100d:0.00}",
        _ => "0.00",
    };
}
