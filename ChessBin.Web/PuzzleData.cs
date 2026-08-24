using System.Text.Json;
using System.Text.Json.Serialization;
using ChessEngine.Engine;

namespace ChessBin.Web;

/// <summary>
/// One puzzle as it ships in <c>wwwroot/puzzles/shard-NNN.json</c>.
/// </summary>
/// <param name="Id">Lichess puzzle id, useful for linking and for de-duplication.</param>
/// <param name="Fen">The position the solver sees — already advanced past the opponent's setup move.</param>
/// <param name="LastMove">The opponent's setup move in UCI, so the board can highlight what just happened.</param>
/// <param name="Solution">
/// The whole line from the solver's turn onward. The solver plays the even indices
/// (0, 2, 4…) and the opponent's replies sit at the odd indices.
/// </param>
public sealed record PuzzleRecord(
    string Id,
    string Fen,
    string LastMove,
    string[] Solution,
    int Rating,
    string[] Themes,
    string Url)
{
    /// <summary>Moves the player actually has to find.</summary>
    public int SolverMoveCount => (Solution.Length + 1) / 2;

    public bool IsSolverPly(int index) => index % 2 == 0;
}

/// <summary>Difficulty filter for practice mode.</summary>
public enum RatingBand
{
    Any,
    Easy,
    Medium,
    Hard,
}

/// <summary>Shape of <c>wwwroot/puzzles/manifest.json</c>.</summary>
public sealed record PuzzleManifest(
    int Version,
    int Count,
    int ShardSize,
    int Shards,
    int[] RatingRange,
    string Source,
    string License);

// Blazor WASM trims on publish, which can strip the reflection metadata that
// reflection-based JSON needs. A source-generated context keeps deserialisation working.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PuzzleManifest))]
[JsonSerializable(typeof(PuzzleRecord[]))]
internal sealed partial class PuzzleJsonContext : JsonSerializerContext;

/// <summary>
/// Reading and addressing the shipped puzzle shards. Deliberately free of any
/// HTTP or browser dependency so it can be exercised directly from tests.
/// </summary>
public static class PuzzleData
{
    /// <summary>The epoch the daily rotation counts from.</summary>
    public static readonly DateOnly Epoch = new(1970, 1, 1);

    public static string ManifestPath => "puzzles/manifest.json";

    /// <summary>
    /// Which puzzle a given day gets. Deterministic and identical for every visitor,
    /// which is what lets the daily puzzle work with no server to agree with.
    /// </summary>
    public static int IndexForDate(DateOnly date, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        int days = date.DayNumber - Epoch.DayNumber;
        return ((days % count) + count) % count;      // stays in range for dates before the epoch
    }

    public static int ShardIndexFor(int puzzleIndex, int shardSize)
    {
        if (shardSize <= 0) throw new ArgumentOutOfRangeException(nameof(shardSize));
        return puzzleIndex / shardSize;
    }

    public static int OffsetWithinShard(int puzzleIndex, int shardSize)
    {
        if (shardSize <= 0) throw new ArgumentOutOfRangeException(nameof(shardSize));
        return puzzleIndex % shardSize;
    }

    public static string ShardPath(int shardIndex) => $"puzzles/shard-{shardIndex:D3}.json";

    /// <summary>
    /// Picks a practice puzzle out of one shard. The importer lays puzzles down round-robin
    /// across rating bands, so every shard holds a spread of all difficulties — which means a
    /// single 8 KB fetch can satisfy any band, with no index of the whole set.
    /// </summary>
    /// <param name="exclude">Puzzle ids already served this session, so practice doesn't repeat itself.</param>
    public static PuzzleRecord? PickPractice(
        IReadOnlyList<PuzzleRecord> shard,
        RatingBand band,
        ISet<string> exclude,
        Random rng)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(exclude);
        ArgumentNullException.ThrowIfNull(rng);

        (int min, int max) = BandRange(band);

        // Preferred: in band and not seen. Then relax, rather than dead-ending on a
        // player who has worked through everything this shard holds.
        var candidates = shard.Where(p => p.Rating >= min && p.Rating <= max && !exclude.Contains(p.Id)).ToArray();
        if (candidates.Length == 0)
            candidates = shard.Where(p => p.Rating >= min && p.Rating <= max).ToArray();
        if (candidates.Length == 0)
            candidates = shard.Where(p => !exclude.Contains(p.Id)).ToArray();
        if (candidates.Length == 0)
            candidates = [.. shard];

        return candidates.Length == 0 ? null : candidates[rng.Next(candidates.Length)];
    }

    public static (int Min, int Max) BandRange(RatingBand band) => band switch
    {
        RatingBand.Easy => (0, 1299),
        RatingBand.Medium => (1300, 1599),
        RatingBand.Hard => (1600, int.MaxValue),
        _ => (0, int.MaxValue),
    };

    public static PuzzleManifest ParseManifest(string json) =>
        JsonSerializer.Deserialize(json, PuzzleJsonContext.Default.PuzzleManifest)
            ?? throw new InvalidDataException("puzzle manifest did not deserialise");

    public static PuzzleRecord[] ParseShard(string json) =>
        JsonSerializer.Deserialize(json, PuzzleJsonContext.Default.PuzzleRecordArray)
            ?? throw new InvalidDataException("puzzle shard did not deserialise");

    /// <summary>
    /// Applies one UCI move to the engine, returning false if it is not legal.
    /// <para>
    /// <see cref="Engine.MovePieceAN"/> only accepts a bare four-character coordinate pair
    /// and ignores its own parse failure, so handing it a promotion such as "e7e8q" would
    /// quietly apply a1a1 instead. Split the promotion suffix off and route it through
    /// <see cref="Engine.PromoteToPieceType"/>.
    /// </para>
    /// </summary>
    public static bool TryApplyUci(Engine engine, string uci)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrEmpty(uci) || uci.Length is not (4 or 5)) return false;

        var promotion = uci.Length == 5
            ? uci[4] switch
            {
                'q' => ChessPieceType.Queen,
                'r' => ChessPieceType.Rook,
                'b' => ChessPieceType.Bishop,
                'n' => ChessPieceType.Knight,
                _ => ChessPieceType.None,
            }
            : ChessPieceType.Queen;

        if (promotion == ChessPieceType.None) return false;

        engine.PromoteToPieceType = promotion;
        return engine.MovePieceAN(uci[..4]);
    }
}
