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

/// <summary>
/// A named opening as the tables give it — the move order it is actually known by, which the
/// position graph cannot supply: transpositions merge move orders there, so the shortest path
/// to a named position is frequently not its line.
/// </summary>
/// <param name="Slug">The URL segment its page lives at, e.g. "sicilian-defense-najdorf-variation".</param>
/// <param name="Sans">The moves in notation, for reading.</param>
/// <param name="Ucis">The same moves in coordinate form, for replaying onto a board.</param>
public sealed record OpeningLine(
    [property: JsonPropertyName("g")] string Slug,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("e")] string Eco,
    [property: JsonPropertyName("p")] IReadOnlyList<string> Sans,
    [property: JsonPropertyName("u")] IReadOnlyList<string> Ucis,
    [property: JsonPropertyName("k")] string Key);

/// <summary>What the line index's manifest says about the shipped set.</summary>
public sealed record OpeningLineManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("shards")] int Shards);

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
    private readonly Dictionary<int, Dictionary<string, OpeningLine>> _lineShards = [];
    private OpeningManifest? _manifest;
    private OpeningLineManifest? _lineManifest;

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
    /// The URL segment an opening's page lives at. Deliberately duplicated from
    /// <c>tools/OpeningPages</c> for the same reason <see cref="ShardOf"/> is, and
    /// <c>OpeningPageTests</c> asserts the two agree on every name that shipped.
    /// <para>
    /// Accents are folded by an explicit table: only eight accented letters occur in the
    /// tables, two of them do not decompose under NFD, and the site is built with
    /// <c>InvariantGlobalization</c>, where normalisation is not something to rely on.
    /// </para>
    /// </summary>
    public static string SlugOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var sb = new System.Text.StringBuilder(name.Length);
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

    /// <summary>
    /// Looks up a named line by the slug in its URL, so a link straight to an opening's page
    /// opens the explorer on that line rather than at the first move. Null when the slug names
    /// nothing, which the caller should treat as "start from the beginning" rather than as an
    /// error — a stale link is not worth an error page.
    /// </summary>
    public async Task<OpeningLine?> LineAsync(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);

        OpeningLineManifest manifest =
            _lineManifest ??= await http.GetFromJsonOrThrowAsync<OpeningLineManifest>("openings/lines/manifest.json");

        int index = ShardOf(slug, manifest.Shards);
        if (!_lineShards.TryGetValue(index, out Dictionary<string, OpeningLine>? shard))
        {
            var lines = await http.GetFromJsonOrThrowAsync<List<OpeningLine>>($"openings/lines/shard-{index:D2}.json");
            shard = _lineShards[index] = lines.ToDictionary(l => l.Slug, StringComparer.Ordinal);
        }

        return shard.GetValueOrDefault(slug);
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
