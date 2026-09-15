using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessBin.Web;

/// <summary>One move playable from a position, and what it is called.</summary>
/// <param name="San">Notation as a player writes it, e.g. "Nf3".</param>
/// <param name="Uci">Coordinate form, e.g. "g1f3" — what actually gets applied to the board.</param>
/// <param name="Weight">
/// Games in the engine's opening book that continued this way. Zero means the book does not
/// cover the move, <em>not</em> that nobody plays it — most named sidelines sit outside a
/// book this size.
/// </param>
/// <param name="Name">The opening the move leads to, when the resulting position is named.</param>
public sealed record OpeningMove(
    [property: JsonPropertyName("s")] string San,
    [property: JsonPropertyName("u")] string Uci,
    [property: JsonPropertyName("w")] int Weight,
    [property: JsonPropertyName("n")] string? Name,
    [property: JsonPropertyName("e")] string? Eco);

/// <summary>A position in the explorer: what it is called, and where it can go.</summary>
public sealed record OpeningPosition(
    [property: JsonPropertyName("k")] string Key,
    [property: JsonPropertyName("n")] string? Name,
    [property: JsonPropertyName("e")] string? Eco,
    [property: JsonPropertyName("m")] IReadOnlyList<OpeningMove> Moves)
{
    public static readonly OpeningPosition Empty = new("", null, null, []);
}

/// <summary>What the manifest says about the shipped set.</summary>
public sealed record OpeningManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("positions")] int Positions,
    [property: JsonPropertyName("named")] int Named,
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("shards")] int Shards);

/// <summary>
/// Reads the committed opening data — see <c>tools/OpeningImport</c>, which generates it.
/// <para>
/// The set is keyed by position rather than by move order, so a line reached by transposition
/// finds the same entry. Shards are fetched on demand and kept for the session: the whole set
/// is about 1.3 MB, which is far too much to pull down to answer one lookup, but a single
/// shard is roughly 20 KB.
/// </para>
/// </summary>
public sealed class OpeningExplorer(HttpClient http)
{
    private readonly Dictionary<int, Dictionary<string, OpeningPosition>> _shards = [];
    private OpeningManifest? _manifest;

    /// <summary>Null until the first lookup has loaded it.</summary>
    public OpeningManifest? Manifest => _manifest;

    /// <summary>
    /// The lookup key: placement, side to move, castling rights and en passant. The halfmove
    /// and fullmove counters are dropped on purpose — they differ between two move orders
    /// that reach the same position, and the explorer wants those to be one entry.
    /// </summary>
    public static string KeyOf(string fen)
    {
        ArgumentNullException.ThrowIfNull(fen);
        string[] f = fen.Split(' ');
        return f.Length >= 4 ? string.Join(' ', f[0], f[1], f[2], f[3]) : fen;
    }

    /// <summary>
    /// Which shard holds a key. Deliberately duplicated from the importer rather than shared:
    /// a console tool cannot reference a Blazor WebAssembly project. <c>OpeningDataTests</c>
    /// asserts the two agree on every key that actually shipped, which is the property that
    /// matters and is stronger than sharing the code would be.
    /// </summary>
    public static int ShardOf(string key, int shards)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (shards <= 0) return 0;

        uint hash = 2166136261;                       // FNV-1a, 32-bit
        foreach (char c in key)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return (int)(hash % (uint)shards);
    }

    /// <summary>
    /// Looks up a position. Returns null when the position is not opening theory at all —
    /// which is the normal answer a few moves out of book, and the caller should say so
    /// rather than treat it as an error.
    /// </summary>
    public async Task<OpeningPosition?> LookupAsync(string fen)
    {
        OpeningManifest manifest = await EnsureManifestAsync();
        string key = KeyOf(fen);

        Dictionary<string, OpeningPosition> shard = await EnsureShardAsync(ShardOf(key, manifest.Shards));
        return shard.GetValueOrDefault(key);
    }

    private async Task<OpeningManifest> EnsureManifestAsync() =>
        _manifest ??= await http.GetFromJsonOrThrowAsync<OpeningManifest>("openings/manifest.json");

    private async Task<Dictionary<string, OpeningPosition>> EnsureShardAsync(int index)
    {
        if (_shards.TryGetValue(index, out var cached)) return cached;

        var positions = await http.GetFromJsonOrThrowAsync<List<OpeningPosition>>($"openings/shard-{index:D3}.json");
        return _shards[index] = positions.ToDictionary(p => p.Key, StringComparer.Ordinal);
    }
}

internal static class OpeningHttpExtensions
{
    /// <summary>
    /// Reads JSON, treating "the server answered with something that is not the file we
    /// asked for" as a failure rather than as null — a 404 rewritten to index.html by a
    /// host would otherwise surface as an empty explorer with no explanation.
    /// </summary>
    internal static async Task<T> GetFromJsonOrThrowAsync<T>(this HttpClient http, string path)
    {
        string body = await http.GetStringAsync(path);
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException($"{path} did not parse as {typeof(T).Name}");
    }
}
