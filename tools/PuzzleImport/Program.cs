using System.Globalization;
using System.Text;
using ChessEngine.Engine;

namespace ChessBin.Tools.PuzzleImport;

/// <summary>
/// Converts the Lichess puzzle database (CC0) into the sharded static JSON that
/// ChessBin's daily puzzle reads. Every puzzle emitted has been replayed through
/// the engine, so a shard that ships is a shard that provably works.
/// </summary>
internal static class Program
{
    // Lichess column order: PuzzleId,FEN,Moves,Rating,RatingDeviation,Popularity,
    //                       NbPlays,Themes,GameUrl,OpeningTags,DailyDate
    private const int ColId = 0, ColFen = 1, ColMoves = 2, ColRating = 3,
                      ColDeviation = 4, ColPopularity = 5, ColPlays = 6,
                      ColThemes = 7, ColUrl = 8, ColCount = 11;

    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt is null) return 2;

        if (!File.Exists(opt.CsvPath))
        {
            Console.Error.WriteLine($"error: CSV not found: {opt.CsvPath}");
            Console.Error.WriteLine("Fetch it with:");
            Console.Error.WriteLine("  curl -L https://database.lichess.org/lichess_db_puzzle.csv.zst | zstd -dc > puzzles.csv");
            return 2;
        }

        // Rating bands give the daily sequence a deliberate difficulty spread instead
        // of 400 easy puzzles followed by 400 hard ones.
        int bandCount = (opt.MaxRating - opt.MinRating + 1) / opt.BandWidth;
        var bands = new List<Candidate>[Math.Max(1, bandCount)];
        for (int i = 0; i < bands.Length; i++) bands[i] = new List<Candidate>();

        var stats = new Stats();
        foreach (var candidate in ReadCandidates(opt, stats))
        {
            int band = Math.Min(bands.Length - 1, (candidate.Rating - opt.MinRating) / opt.BandWidth);
            bands[band].Add(candidate);
        }

        // Stable order inside each band so re-running the importer is byte-identical.
        foreach (var band in bands)
            band.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        Console.WriteLine($"read      {stats.Rows:n0} rows");
        Console.WriteLine($"rejected  {stats.RejectedCheap:n0} on rating/quality/length filters");
        Console.WriteLine($"candidates {bands.Sum(b => b.Count):n0} across {bands.Length} rating bands");

        var chosen = SelectAndValidate(bands, opt, stats);

        Console.WriteLine($"validated {chosen.Count:n0} puzzles ({stats.RejectedEngine:n0} rejected by the engine)");
        if (stats.MateVerified > 0)
            Console.WriteLine($"          {stats.MateVerified:n0} mate puzzles confirmed checkmate at the end of the line");

        if (chosen.Count == 0)
        {
            Console.Error.WriteLine("error: nothing survived filtering — loosen the thresholds");
            return 1;
        }

        if (chosen.Count < opt.Count)
            Console.WriteLine($"note      wanted {opt.Count:n0}, only {chosen.Count:n0} available; shipping what passed");

        Write(chosen, opt);
        return 0;
    }

    // ── Reading + cheap filtering ───────────────────────────────────────────────

    private static IEnumerable<Candidate> ReadCandidates(Options opt, Stats stats)
    {
        using var reader = new StreamReader(opt.CsvPath);
        string? line = reader.ReadLine();               // header
        if (line is null || !line.StartsWith("PuzzleId", StringComparison.Ordinal))
            throw new InvalidDataException("unexpected CSV header — is this the Lichess puzzle export?");

        while ((line = reader.ReadLine()) is not null)
        {
            stats.Rows++;
            var f = line.Split(',');
            if (f.Length < ColCount) { stats.RejectedCheap++; continue; }

            if (!int.TryParse(f[ColRating], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rating) ||
                !int.TryParse(f[ColDeviation], NumberStyles.Integer, CultureInfo.InvariantCulture, out int deviation) ||
                !int.TryParse(f[ColPopularity], NumberStyles.Integer, CultureInfo.InvariantCulture, out int popularity) ||
                !int.TryParse(f[ColPlays], NumberStyles.Integer, CultureInfo.InvariantCulture, out int plays))
            { stats.RejectedCheap++; continue; }

            if (rating < opt.MinRating || rating > opt.MaxRating ||
                deviation > opt.MaxDeviation || popularity < opt.MinPopularity || plays < opt.MinPlays)
            { stats.RejectedCheap++; continue; }

            var moves = f[ColMoves].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // moves[0] is the opponent's blunder; the solver's reply is moves[1].
            // So a line of length n leaves n-1 plies, of which the solver plays every other one.
            int solverMoves = (moves.Length - 1 + 1) / 2;
            if (moves.Length < 2 || solverMoves < opt.MinSolverMoves || solverMoves > opt.MaxSolverMoves)
            { stats.RejectedCheap++; continue; }

            yield return new Candidate(
                f[ColId], f[ColFen], moves, rating,
                f[ColThemes].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                f[ColUrl]);
        }
    }

    // ── Selection + engine validation ───────────────────────────────────────────

    private static List<Puzzle> SelectAndValidate(List<Candidate>[] bands, Options opt, Stats stats)
    {
        // One Engine reused for every puzzle: the constructor loads the 1,537-position
        // opening book, and paying that per row would dominate the whole run.
        var engine = new Engine();
        var chosen = new List<Puzzle>(opt.Count);
        var cursor = new int[bands.Length];

        // Round-robin across bands so consecutive days alternate difficulty.
        bool progress = true;
        while (chosen.Count < opt.Count && progress)
        {
            progress = false;
            for (int b = 0; b < bands.Length && chosen.Count < opt.Count; b++)
            {
                var band = bands[b];
                while (cursor[b] < band.Count)
                {
                    var candidate = band[cursor[b]++];
                    progress = true;
                    var puzzle = Validate(engine, candidate, stats);
                    if (puzzle is not null) { chosen.Add(puzzle); break; }
                }
            }
        }

        return chosen;
    }

    /// <summary>
    /// Replays a candidate through the engine: applies the opponent's setup move to get
    /// the position the solver actually sees, then plays the whole solution line to prove
    /// every move is legal. Returns null if anything doesn't hold.
    /// </summary>
    private static Puzzle? Validate(Engine engine, Candidate c, Stats stats)
    {
        try
        {
            engine.SetPosition(c.Fen);

            // The opponent's move creates the puzzle. Everything after it is the solution.
            if (!ApplyUci(engine, c.Moves[0])) { stats.RejectedEngine++; return null; }

            string puzzleFen = engine.FEN;

            for (int i = 1; i < c.Moves.Length; i++)
            {
                if (!ApplyUci(engine, c.Moves[i])) { stats.RejectedEngine++; return null; }
            }

            // A puzzle Lichess labels as mate must actually end in mate. This is the one
            // check that exercises FEN parsing, move application and mate detection
            // together, so it's worth asserting rather than trusting.
            bool claimsMate = c.Themes.Any(t => t.StartsWith("mateIn", StringComparison.Ordinal) || t == "mate");
            if (claimsMate)
            {
                if (!engine.IsGameOver()) { stats.RejectedEngine++; return null; }
                stats.MateVerified++;
            }

            return new Puzzle(c.Id, puzzleFen, c.Moves[0], c.Moves[1..], c.Rating, c.Themes, c.Url);
        }
        catch (Exception)
        {
            // A FEN the parser rejects, or a move that throws, is simply not shippable.
            stats.RejectedEngine++;
            return null;
        }
    }

    /// <summary>
    /// Applies one UCI move. MovePieceAN only accepts a bare 4-character coordinate pair
    /// and silently ignores ParseAN's failure, so a promotion like "e7e8q" would be applied
    /// as a1a1. Split the promotion suffix off and route it through PromoteToPieceType.
    /// </summary>
    private static bool ApplyUci(Engine engine, string uci)
    {
        if (uci.Length is not (4 or 5)) return false;

        engine.PromoteToPieceType = uci.Length == 5
            ? uci[4] switch
            {
                'q' => ChessPieceType.Queen,
                'r' => ChessPieceType.Rook,
                'b' => ChessPieceType.Bishop,
                'n' => ChessPieceType.Knight,
                _ => ChessPieceType.None,
            }
            : ChessPieceType.Queen;

        if (engine.PromoteToPieceType == ChessPieceType.None) return false;

        return engine.MovePieceAN(uci[..4]);
    }

    // ── Output ──────────────────────────────────────────────────────────────────

    private static void Write(List<Puzzle> puzzles, Options opt)
    {
        Directory.CreateDirectory(opt.OutDir);
        foreach (var stale in Directory.EnumerateFiles(opt.OutDir, "shard-*.json")) File.Delete(stale);

        int shards = (puzzles.Count + opt.ShardSize - 1) / opt.ShardSize;
        for (int s = 0; s < shards; s++)
        {
            var slice = puzzles.Skip(s * opt.ShardSize).Take(opt.ShardSize).ToList();
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < slice.Count; i++)
            {
                sb.Append(slice[i].ToJson());
                sb.Append(i == slice.Count - 1 ? "\n" : ",\n");   // one puzzle per line = readable diffs
            }
            sb.Append("]\n");
            File.WriteAllText(Path.Combine(opt.OutDir, $"shard-{s:D3}.json"), sb.ToString());
        }

        // No timestamp on purpose: re-running the importer on the same input must produce
        // byte-identical files, otherwise every run shows up as a diff.
        var manifest = new StringBuilder();
        manifest.Append("{\n");
        manifest.Append($"  \"version\": 1,\n");
        manifest.Append($"  \"count\": {puzzles.Count},\n");
        manifest.Append($"  \"shardSize\": {opt.ShardSize},\n");
        manifest.Append($"  \"shards\": {shards},\n");
        manifest.Append($"  \"ratingRange\": [{puzzles.Min(p => p.Rating)}, {puzzles.Max(p => p.Rating)}],\n");
        manifest.Append("  \"source\": \"lichess_db_puzzle.csv — database.lichess.org\",\n");
        manifest.Append("  \"license\": \"CC0-1.0\",\n");
        manifest.Append("  \"fen\": \"position the solver sees, after the opponent's setup move\",\n");
        manifest.Append("  \"solution\": \"full line from the solver's turn; solver plays even indices, opponent replies odd\"\n");
        manifest.Append("}\n");
        File.WriteAllText(Path.Combine(opt.OutDir, "manifest.json"), manifest.ToString());

        long bytes = Directory.EnumerateFiles(opt.OutDir, "*.json").Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"wrote     {shards} shards + manifest to {opt.OutDir} ({bytes / 1024.0:n1} KB total)");
    }

    // ── Types ───────────────────────────────────────────────────────────────────

    private sealed record Candidate(string Id, string Fen, string[] Moves, int Rating, string[] Themes, string Url);

    private sealed record Puzzle(string Id, string Fen, string LastMove, string[] Solution,
                                 int Rating, string[] Themes, string Url)
    {
        public string ToJson()
        {
            var sol = string.Join(",", Solution.Select(m => $"\"{m}\""));
            var themes = string.Join(",", Themes.Select(t => $"\"{t}\""));
            return $"{{\"id\":\"{Id}\",\"fen\":\"{Fen}\",\"lastMove\":\"{LastMove}\"," +
                   $"\"solution\":[{sol}],\"rating\":{Rating},\"themes\":[{themes}],\"url\":\"{Url}\"}}";
        }
    }

    private sealed class Stats
    {
        public int Rows, RejectedCheap, RejectedEngine, MateVerified;
    }

    private sealed class Options
    {
        public string CsvPath = "";
        public string OutDir = "";
        public int Count = 3650;          // ten years of dailies
        public int ShardSize = 128;
        public int MinRating = 1000, MaxRating = 1899, BandWidth = 100;
        public int MaxDeviation = 90;     // rating has settled
        public int MinPopularity = 90;    // players liked it
        public int MinPlays = 1000;       // well tested
        public int MinSolverMoves = 1, MaxSolverMoves = 3;

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "--csv": o.CsvPath = Next(); break;
                    case "--out": o.OutDir = Next(); break;
                    case "--count": o.Count = int.Parse(Next()); break;
                    case "--shard": o.ShardSize = int.Parse(Next()); break;
                    case "--min-rating": o.MinRating = int.Parse(Next()); break;
                    case "--max-rating": o.MaxRating = int.Parse(Next()); break;
                    case "--max-solver-moves": o.MaxSolverMoves = int.Parse(Next()); break;
                    default:
                        Console.Error.WriteLine($"unknown argument: {a}");
                        return null;
                }
            }

            if (o.CsvPath.Length == 0 || o.OutDir.Length == 0)
            {
                Console.Error.WriteLine("usage: PuzzleImport --csv <lichess_db_puzzle.csv> --out <dir>");
                Console.Error.WriteLine("       [--count 3650] [--shard 128] [--min-rating 1000] [--max-rating 1899]");
                Console.Error.WriteLine("       [--max-solver-moves 3]");
                return null;
            }
            return o;
        }
    }
}
