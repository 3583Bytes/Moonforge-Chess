using System.Text;
using System.Text.RegularExpressions;
using ChessEngine.Engine;

namespace ChessBin.Tools.OpeningImport;

/// <summary>
/// Converts the Lichess <c>chess-openings</c> tables (CC0) into the sharded static JSON the
/// opening explorer reads, keyed by position so transpositions resolve to the same entry.
/// <para>
/// Every line is replayed through <see cref="Engine"/>, so a line that ships is a line that
/// provably plays. Popularity comes from the engine's own opening book, which carries real
/// frequency counts — the Lichess explorer API, which would also give win/draw/loss, needs
/// credentials this importer deliberately does not hold.
/// </para>
/// </summary>
internal static class Program
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt is null) return 2;

        string[] tables = ["a", "b", "c", "d", "e"];
        var missing = tables.Where(t => !File.Exists(Path.Combine(opt.TsvDir, $"{t}.tsv"))).ToArray();
        if (missing.Length > 0)
        {
            Console.Error.WriteLine($"error: missing {string.Join(", ", missing.Select(m => m + ".tsv"))} in {opt.TsvDir}");
            Console.Error.WriteLine("Fetch them with:");
            Console.Error.WriteLine("  for f in a b c d e; do curl -sLO https://raw.githubusercontent.com/lichess-org/chess-openings/master/$f.tsv; done");
            return 2;
        }

        var stats = new Stats();
        var positions = new Dictionary<string, Position>(StringComparer.Ordinal);

        // One entry per distinct opening name, holding the move order the table gives it.
        // The position graph cannot supply this after the fact: transpositions merge move
        // orders, so the shortest path to a named position is often not the line the opening
        // is known by — walking the graph reaches "Queen's Gambit Declined" via 1.d4 Nf6,
        // and 1,302 named positions have more than one shortest path. The table's own move
        // order is the only canonical one, so it is captured here rather than rediscovered.
        var lines = new Dictionary<string, Line>(StringComparer.Ordinal);

        // One Engine for the whole run: its constructor loads the opening book, and paying
        // that per line would dominate everything else.
        var engine = new Engine();

        foreach (string table in tables)
        {
            foreach (var line in ReadTable(Path.Combine(opt.TsvDir, $"{table}.tsv"), stats))
            {
                Ingest(engine, line, positions, lines, stats);
            }
        }

        Console.WriteLine($"read      {stats.Rows:n0} named openings ({stats.RejectedRow:n0} malformed rows)");
        Console.WriteLine($"replayed  {stats.Accepted:n0} lines ({stats.RejectedEngine:n0} rejected by the engine)");
        Console.WriteLine($"positions {positions.Count:n0} unique, {positions.Values.Sum(p => p.Moves.Count):n0} moves");
        Console.WriteLine($"lines     {lines.Count:n0} distinct names, each with the move order its table row gives");

        int weighted = ApplyBookWeights(opt.BookPath, positions);
        Console.WriteLine($"weights   {weighted:n0} moves carry a popularity count from the engine book");

        if (positions.Count == 0)
        {
            Console.Error.WriteLine("error: nothing survived — is the TSV directory right?");
            return 1;
        }

        Write(positions, lines, opt, stats);
        return 0;
    }

    // ── Reading ─────────────────────────────────────────────────────────────────

    /// <summary>Rows of the ECO table: an ECO code, a name, and the line in SAN.</summary>
    private static IEnumerable<Row> ReadTable(string path, Stats stats)
    {
        using var reader = new StreamReader(path);
        string? line = reader.ReadLine();
        if (line is null || !line.StartsWith("eco\tname\tpgn", StringComparison.Ordinal))
            throw new InvalidDataException($"unexpected header in {path} — is this lichess-org/chess-openings?");

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            stats.Rows++;

            string[] f = line.Split('\t');
            if (f.Length < 3 || f[0].Length == 0 || f[1].Length == 0 || f[2].Length == 0)
            {
                stats.RejectedRow++;
                continue;
            }

            yield return new Row(f[0], f[1], f[2]);
        }
    }

    /// <summary>
    /// "1. e4 g5 2. d4 Bg7" becomes the four SAN moves. Move numbers appear both detached
    /// ("1." ) and glued ("1.e4") across the tables, so both are stripped here.
    /// </summary>
    private static List<string> SanMoves(string pgn)
    {
        var moves = new List<string>();
        foreach (string token in pgn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string san = Regex.Replace(token, @"^\d+\.+", "");
            if (san.Length > 0) moves.Add(san);
        }
        return moves;
    }

    // ── Building the position graph ─────────────────────────────────────────────

    /// <summary>
    /// Replays one named line, recording every position along it and the move that leaves
    /// it. The line's final position carries the name — that is what makes the explorer
    /// able to say "you are in the Najdorf" rather than just listing moves.
    /// </summary>
    private static void Ingest(
        Engine engine,
        Row row,
        Dictionary<string, Position> positions,
        Dictionary<string, Line> lines,
        Stats stats)
    {
        var sans = SanMoves(row.Pgn);
        if (sans.Count == 0) { stats.RejectedEngine++; return; }

        try
        {
            engine.SetPosition(StartFen);

            string key = Key(engine.FEN);
            var ucis = new List<string>(sans.Count);
            foreach (string san in sans)
            {
                string from = engine.FEN;
                if (!SanMove.TryApply(engine, san)) { stats.RejectedEngine++; return; }

                string uci = UciOf(from, engine.FEN, engine);
                if (uci.Length == 0) { stats.RejectedEngine++; return; }
                ucis.Add(uci);

                Position node = positions.TryGetValue(key, out var existing)
                    ? existing
                    : positions[key] = new Position(key);

                string childKey = Key(engine.FEN);
                node.Add(san, uci, childKey);
                key = childKey;
            }

            // The terminal position is the named one. A position can be the end of more than
            // one row (the tables list both "Sicilian Defense" and longer variations that
            // transpose); first name wins, and rows are read in ECO order, so the answer is
            // stable across runs.
            Position terminal = positions.TryGetValue(key, out var t) ? t : positions[key] = new Position(key);
            terminal.Name ??= row.Name;
            terminal.Eco ??= row.Eco;

            // First row wins per name, the same rule as the terminal name above, so a page's
            // line is the one the table lists first. Rows are read in ECO order a→e, so the
            // choice is stable across runs.
            if (!lines.ContainsKey(row.Name))
                lines[row.Name] = new Line(Slug(row.Name), row.Name, row.Eco, sans, ucis, key);

            stats.Accepted++;
        }
        catch (Exception)
        {
            // A line the engine will not replay is simply not shippable.
            stats.RejectedEngine++;
        }
    }

    /// <summary>
    /// The lookup key: placement, side to move, castling rights and en-passant square — the
    /// first four FEN fields. The halfmove and fullmove counters are dropped on purpose, so
    /// two move orders reaching the same position share one entry.
    /// </summary>
    internal static string Key(string fen)
    {
        string[] f = fen.Split(' ');
        return f.Length >= 4 ? string.Join(' ', f[0], f[1], f[2], f[3]) : fen;
    }

    /// <summary>
    /// Recovers the coordinate form of the move just played. The engine reports its last
    /// move's source and destination, which is cheaper and safer than re-deriving them by
    /// diffing two FENs (castling and en passant move two pieces).
    /// </summary>
    private static string UciOf(string beforeFen, string afterFen, Engine engine)
    {
        MoveContent last = engine.LastMove;
        if (last is null) return "";

        string square = Square(last.MovingPiecePrimary.SrcPosition) + Square(last.MovingPiecePrimary.DstPosition);
        return last.PawnPromotedTo switch
        {
            ChessPieceType.Queen => square + "q",
            ChessPieceType.Rook => square + "r",
            ChessPieceType.Bishop => square + "b",
            ChessPieceType.Knight => square + "n",
            _ => square,
        };
    }

    /// <summary>Board index to algebraic. Index 0 is a8 and 63 is h1 — see CLAUDE.md.</summary>
    private static string Square(byte index) =>
        $"{(char)('a' + index % 8)}{8 - index / 8}";

    // ── Popularity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the frequency counts out of the engine's opening book. <c>Book.cs</c> is a
    /// generated C# literal — <c>StartingFEN</c> followed by a move line of
    /// <c>e2e4{3820}</c> entries — and it is parsed as text rather than referenced, because
    /// the type is internal to the engine and this is the only consumer that wants the raw
    /// counts. Positions the book does not know simply carry no weight.
    /// </summary>
    private static int ApplyBookWeights(string bookPath, Dictionary<string, Position> positions)
    {
        if (!File.Exists(bookPath))
        {
            Console.Error.WriteLine($"warning: {bookPath} not found — shipping without popularity counts");
            return 0;
        }

        string source = File.ReadAllText(bookPath);
        var entries = Regex.Matches(
            source,
            """StartingFEN\s*=\s*@"(?<fen>[^"]+)";\s*moveLine\s*=\s*@"(?<moves>[^"]*)";""",
            RegexOptions.Singleline);

        int applied = 0;
        foreach (Match entry in entries)
        {
            if (!positions.TryGetValue(Key(entry.Groups["fen"].Value.Trim()), out Position? node)) continue;

            foreach (string token in entry.Groups["moves"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Match m = Regex.Match(token, @"^(?<uci>[a-h][1-8][a-h][1-8][qrbn]?)(?:\{(?<n>\d+)\})?$");
                if (!m.Success) continue;

                int weight = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : 1;
                if (node.SetWeight(m.Groups["uci"].Value, weight)) applied++;
            }
        }

        return applied;
    }

    // ── Output ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shards by a hash of the position key rather than sequentially: a lookup knows which
    /// single file to fetch without an index. <see cref="Shard"/> is duplicated in
    /// ChessBin.Web's reader, and OpeningDataTests asserts the two agree on every shipped key.
    /// </summary>
    internal static int Shard(string key, int shards)
    {
        uint hash = 2166136261;                       // FNV-1a, 32-bit
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return (int)(hash % (uint)shards);
    }

    /// <summary>
    /// The URL segment an opening's page lives at: "Queen's Pawn Game: Zukertort Variation"
    /// becomes "queens-pawn-game-zukertort-variation".
    /// <para>
    /// Accents are folded by an explicit table rather than Unicode normalisation. Exactly
    /// eight accented letters occur in the tables — á ä é ó ö ø ü ć — and two of them, ø and
    /// ć, do not decompose under NFD at all, so normalisation would both miss those and drag
    /// in a dependency on ICU for a job a lookup does identically on every platform.
    /// </para>
    /// Apostrophes are dropped rather than turned into a separator, so "King's Indian" reads
    /// "kings-indian" and not "king-s-indian".
    /// </summary>
    internal static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool separatorPending = false;

        foreach (char raw in name)
        {
            char lower = char.ToLowerInvariant(raw);
            char c = lower switch
            {
                'á' => 'a', 'ä' => 'a', 'é' => 'e', 'ó' => 'o',
                'ö' => 'o', 'ø' => 'o', 'ü' => 'u', 'ć' => 'c',
                _ => lower,
            };

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separatorPending && sb.Length > 0) sb.Append('-');
                separatorPending = false;
                sb.Append(c);
            }
            else if (c != '\'')
            {
                separatorPending = true;
            }
        }

        return sb.ToString();
    }

    private static void Write(Dictionary<string, Position> positions, Dictionary<string, Line> lines, Options opt, Stats stats)
    {
        Directory.CreateDirectory(opt.OutDir);
        foreach (string stale in Directory.EnumerateFiles(opt.OutDir, "shard-*.json")) File.Delete(stale);

        // Names are resolved after the whole graph exists, so a move can say what it leads
        // to without the reader needing a second fetch to find out.
        foreach (Position node in positions.Values)
        {
            foreach (Move move in node.Moves)
            {
                if (!positions.TryGetValue(move.ChildKey, out Position? child)) continue;
                move.Name = child.Name;
                move.Eco = child.Eco;
            }
        }

        var buckets = new List<Position>[opt.Shards];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = [];
        foreach (Position node in positions.Values) buckets[Shard(node.Key, opt.Shards)].Add(node);

        for (int s = 0; s < buckets.Length; s++)
        {
            // Ordinal sort inside the shard so a re-run on the same input is byte-identical.
            buckets[s].Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            var sb = new StringBuilder("[\n");
            for (int i = 0; i < buckets[s].Count; i++)
            {
                sb.Append(buckets[s][i].ToJson());
                sb.Append(i == buckets[s].Count - 1 ? "\n" : ",\n");   // one position per line = readable diffs
            }
            sb.Append("]\n");
            File.WriteAllText(Path.Combine(opt.OutDir, $"shard-{s:D3}.json"), sb.ToString());
        }

        // No timestamp on purpose: re-running on the same input must produce byte-identical
        // files, or every run shows up as a diff.
        var manifest = new StringBuilder();
        manifest.Append("{\n");
        manifest.Append("  \"version\": 1,\n");
        manifest.Append($"  \"positions\": {positions.Count},\n");
        manifest.Append($"  \"moves\": {positions.Values.Sum(p => p.Moves.Count)},\n");
        manifest.Append($"  \"named\": {positions.Values.Count(p => p.Name is not null)},\n");
        manifest.Append($"  \"lines\": {stats.Accepted},\n");
        manifest.Append($"  \"shards\": {opt.Shards},\n");
        manifest.Append("  \"shardBy\": \"FNV-1a 32-bit over the key, modulo shards\",\n");
        manifest.Append("  \"key\": \"first four FEN fields — placement, side, castling, en passant\",\n");
        manifest.Append("  \"source\": \"lichess-org/chess-openings (names, ECO) + the engine's own opening book (popularity)\",\n");
        manifest.Append("  \"license\": \"CC0-1.0\",\n");
        manifest.Append("  \"weight\": \"games in the engine book that continued this way; 0 means the book does not cover it\"\n");
        manifest.Append("}\n");
        File.WriteAllText(Path.Combine(opt.OutDir, "manifest.json"), manifest.ToString());

        long bytes = Directory.EnumerateFiles(opt.OutDir, "*.json").Sum(f => new FileInfo(f).Length);
        int biggest = buckets.Max(b => b.Count);
        Console.WriteLine($"wrote     {opt.Shards} shards + manifest to {opt.OutDir} ({bytes / 1024.0:n1} KB total, largest shard {biggest} positions)");

        WriteLines(lines, opt);
    }

    /// <summary>
    /// The name index, sharded the same way the positions are so a deep link fetches one
    /// small file instead of a 400 KB table. Sharded by slug rather than by position key,
    /// because a slug is all a URL carries.
    /// </summary>
    private static void WriteLines(Dictionary<string, Line> lines, Options opt)
    {
        string dir = Path.Combine(opt.OutDir, "lines");
        Directory.CreateDirectory(dir);
        foreach (string stale in Directory.EnumerateFiles(dir, "shard-*.json")) File.Delete(stale);

        var buckets = new List<Line>[opt.LineShards];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = [];
        foreach (Line line in lines.Values) buckets[Shard(line.Slug, opt.LineShards)].Add(line);

        for (int s = 0; s < buckets.Length; s++)
        {
            buckets[s].Sort((a, b) => string.CompareOrdinal(a.Slug, b.Slug));

            var sb = new StringBuilder("[\n");
            for (int i = 0; i < buckets[s].Count; i++)
            {
                sb.Append(buckets[s][i].ToJson());
                sb.Append(i == buckets[s].Count - 1 ? "\n" : ",\n");   // one line per line = readable diffs
            }
            sb.Append("]\n");
            File.WriteAllText(Path.Combine(dir, $"shard-{s:D2}.json"), sb.ToString());
        }

        var manifest = new StringBuilder();
        manifest.Append("{\n");
        manifest.Append("  \"version\": 1,\n");
        manifest.Append($"  \"lines\": {lines.Count},\n");
        manifest.Append($"  \"shards\": {opt.LineShards},\n");
        manifest.Append("  \"shardBy\": \"FNV-1a 32-bit over the slug, modulo shards\",\n");
        manifest.Append("  \"slug\": \"the name lowercased, accents folded, apostrophes dropped, every other run of characters collapsed to one dash\",\n");
        manifest.Append("  \"line\": \"p is the move order in notation, u the same moves in coordinates, k the position it ends on\",\n");
        manifest.Append("  \"source\": \"lichess-org/chess-openings\",\n");
        manifest.Append("  \"license\": \"CC0-1.0\"\n");
        manifest.Append("}\n");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToString());

        long bytes = Directory.EnumerateFiles(dir, "*.json").Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"wrote     {opt.LineShards} line shards + manifest to {dir} ({bytes / 1024.0:n1} KB total)");
    }

    // ── Types ───────────────────────────────────────────────────────────────────

    private sealed record Row(string Eco, string Name, string Pgn);

    /// <summary>
    /// A named opening as the table gives it: the slug its page lives at, the move order in
    /// both notations, and the position the line ends on. The coordinate form is what the
    /// explorer replays to open on that line; the notation is what a page prints.
    /// </summary>
    private sealed record Line(
        string Slug,
        string Name,
        string Eco,
        List<string> Sans,
        List<string> Ucis,
        string Key)
    {
        public string ToJson()
        {
            static string Quote(IEnumerable<string> values) =>
                string.Join(",", values.Select(v => $"\"{Esc(v)}\""));

            return $"{{\"g\":\"{Esc(Slug)}\",\"n\":\"{Esc(Name)}\",\"e\":\"{Esc(Eco)}\"," +
                   $"\"p\":[{Quote(Sans)}],\"u\":[{Quote(Ucis)}],\"k\":\"{Esc(Key)}\"}}";
        }
    }

    private sealed class Move(string san, string uci, string childKey)
    {
        public string San { get; } = san;
        public string Uci { get; } = uci;
        public string ChildKey { get; } = childKey;
        public int Weight { get; set; }
        public string? Name { get; set; }
        public string? Eco { get; set; }
    }

    private sealed class Position(string key)
    {
        public string Key { get; } = key;
        public string? Name { get; set; }
        public string? Eco { get; set; }
        public List<Move> Moves { get; } = [];

        public void Add(string san, string uci, string childKey)
        {
            if (Moves.Any(m => m.Uci == uci)) return;
            Moves.Add(new Move(san, uci, childKey));
        }

        public bool SetWeight(string uci, int weight)
        {
            Move? move = Moves.FirstOrDefault(m => m.Uci == uci);
            if (move is null) return false;
            move.Weight = weight;
            return true;
        }

        public string ToJson()
        {
            // Most played first, then by notation, so the list is stable and reads the way a
            // player expects without the UI having to sort it.
            var ordered = Moves
                .OrderByDescending(m => m.Weight)
                .ThenBy(m => m.San, StringComparer.Ordinal)
                .Select(m =>
                {
                    var sb = new StringBuilder($"{{\"s\":\"{Esc(m.San)}\",\"u\":\"{m.Uci}\",\"w\":{m.Weight}");
                    if (m.Name is not null) sb.Append($",\"n\":\"{Esc(m.Name)}\",\"e\":\"{Esc(m.Eco!)}\"");
                    return sb.Append('}').ToString();
                });

            var line = new StringBuilder($"{{\"k\":\"{Esc(Key)}\"");
            if (Name is not null) line.Append($",\"n\":\"{Esc(Name)}\",\"e\":\"{Esc(Eco!)}\"");
            return line.Append($",\"m\":[{string.Join(",", ordered)}]}}").ToString();
        }
    }

    /// <summary>Opening names carry quotes and the odd backslash; FENs carry neither, but escape both anyway.</summary>
    private static string Esc(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class Stats
    {
        public int Rows, RejectedRow, RejectedEngine, Accepted;
    }

    private sealed class Options
    {
        public string TsvDir = "";
        public string OutDir = "";
        public string BookPath = Path.Combine("ChessCoreEngine", "Book.cs");
        public int Shards = 64;
        public int LineShards = 64;

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "--tsv": o.TsvDir = Next(); break;
                    case "--out": o.OutDir = Next(); break;
                    case "--book": o.BookPath = Next(); break;
                    case "--shards": o.Shards = int.Parse(Next()); break;
                    case "--line-shards": o.LineShards = int.Parse(Next()); break;
                    default:
                        Console.Error.WriteLine($"unknown argument: {a}");
                        return null;
                }
            }

            if (o.TsvDir.Length == 0 || o.OutDir.Length == 0)
            {
                Console.Error.WriteLine("usage: OpeningImport --tsv <dir with a.tsv..e.tsv> --out <dir>");
                Console.Error.WriteLine("       [--book ChessCoreEngine/Book.cs] [--shards 64] [--line-shards 64]");
                return null;
            }
            return o;
        }
    }
}
