using System.Text;
using System.Text.RegularExpressions;
using ChessEngine.Engine;

namespace ChessBin.Web;

/// <summary>A game read out of a PGN, in the shape the reviewer already consumes.</summary>
public sealed record PgnGame(
    string White,
    string Black,
    string Result,
    string Date,
    string Event,
    string StartFen,
    IReadOnlyList<PlayedMove> Moves)
{
    public bool StartsFromStandardPosition => StartFen == ChessGameSession.StartingFen;
    public string Title => string.IsNullOrWhiteSpace(White) || string.IsNullOrWhiteSpace(Black)
        ? "Imported game"
        : $"{White} vs {Black}";
}

public sealed record PgnImportResult(PgnGame? Game, string? Error)
{
    public bool Success => Game is not null;
    public static PgnImportResult Fail(string error) => new(null, error);
}

/// <summary>
/// Reads a PGN into moves the reviewer can walk.
/// <para>
/// Deliberately a pragmatic subset: tag pairs, the mainline, castling, promotions and check
/// marks are honoured; comments, recursive variations and NAGs are skipped rather than
/// modelled. That covers what Lichess and Chess.com actually export, which is the point —
/// <see cref="PGN"/> can write PGN but nothing could read it back.
/// </para>
/// </summary>
public static class PgnImport
{
    /// <summary>A guard so a pasted book doesn't lock the tab up.</summary>
    public const int MaxMoves = 600;

    private static readonly Regex TagPair = new(@"^\s*\[\s*(\w+)\s*""([^""]*)""\s*\]\s*$", RegexOptions.Compiled);

    public static PgnImportResult Parse(string pgn)
    {
        if (string.IsNullOrWhiteSpace(pgn))
            return PgnImportResult.Fail("Paste a game first.");

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var movetext = new StringBuilder();

        foreach (string raw in pgn.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            Match tag = TagPair.Match(line);
            if (tag.Success) tags[tag.Groups[1].Value] = tag.Groups[2].Value;
            else movetext.Append(line).Append(' ');
        }

        string startFen = tags.TryGetValue("FEN", out string? fen) && !string.IsNullOrWhiteSpace(fen)
            ? fen.Trim()
            : ChessGameSession.StartingFen;

        var tokens = Tokenise(movetext.ToString());
        if (tokens.Count == 0)
            return PgnImportResult.Fail("No moves found. Make sure you pasted the moves, not just the game details.");

        Engine engine;
        try
        {
            engine = new Engine(startFen);
            engine.GenerateValidMoves();
        }
        catch (Exception)
        {
            return PgnImportResult.Fail("The starting position in this game's FEN tag could not be read.");
        }

        var moves = new List<PlayedMove>();
        foreach (string token in tokens)
        {
            if (moves.Count >= MaxMoves)
                return PgnImportResult.Fail($"That game is longer than {MaxMoves} moves — is it more than one game?");

            PlayedMove? applied = ApplySan(engine, token);
            if (applied is null)
            {
                return PgnImportResult.Fail(moves.Count == 0
                    ? $"Could not read the first move, \"{token}\". Is this standard algebraic notation?"
                    : $"Could not play move {moves.Count + 1}, \"{token}\", in the position it arrives at. " +
                      "The game may contain a non-standard notation or an illegal move.");
            }
            moves.Add(applied);
        }

        return new PgnImportResult(new PgnGame(
            White: tags.GetValueOrDefault("White", ""),
            Black: tags.GetValueOrDefault("Black", ""),
            Result: tags.GetValueOrDefault("Result", "*"),
            Date: tags.GetValueOrDefault("Date", ""),
            Event: tags.GetValueOrDefault("Event", ""),
            StartFen: startFen,
            Moves: moves), null);
    }

    /// <summary>
    /// Strips everything that isn't a move: brace and semicolon comments, recursive
    /// variations, NAGs, move numbers and the result token.
    /// </summary>
    private static List<string> Tokenise(string movetext)
    {
        var cleaned = new StringBuilder(movetext.Length);
        int braceDepth = 0, parenDepth = 0;

        for (int i = 0; i < movetext.Length; i++)
        {
            char c = movetext[i];
            if (c == '{') { braceDepth++; continue; }
            if (c == '}') { braceDepth = Math.Max(0, braceDepth - 1); continue; }
            if (braceDepth > 0) continue;

            // Variations nest, so track depth rather than matching the first close paren.
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { parenDepth = Math.Max(0, parenDepth - 1); continue; }
            if (parenDepth > 0) continue;

            if (c == ';') { while (i < movetext.Length && movetext[i] != '\n') i++; continue; }

            cleaned.Append(c);
        }

        var tokens = new List<string>();
        foreach (string piece in cleaned.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = piece.Trim();
            if (t.Length == 0) continue;
            if (t[0] == '$') continue;                                    // NAG
            if (t is "1-0" or "0-1" or "1/2-1/2" or "*") continue;        // result
            if (char.IsDigit(t[0]) && t.TrimEnd('.').All(char.IsDigit)) continue;   // "12." / "12..." / "12"

            // Lichess writes "12...Nf6" as one token when the black move follows a comment.
            int dots = t.IndexOf("...", StringComparison.Ordinal);
            if (dots > 0 && char.IsDigit(t[0])) t = t[(dots + 3)..];
            else if (t.Contains('.') && char.IsDigit(t[0])) t = t[(t.LastIndexOf('.') + 1)..];

            if (t.Length > 0) tokens.Add(t);
        }

        return tokens;
    }

    private static PlayedMove? ApplySan(Engine engine, string sanToken)
    {
        // Resolution lives in the engine so the vote referee can use it too; PgnImport only
        // needs to record what was played.
        if (!SanMove.TryApply(engine, sanToken)) return null;

        MoveContent last = engine.LastMove;
        byte from = last.MovingPiecePrimary.SrcPosition;
        byte to = last.MovingPiecePrimary.DstPosition;

        string uci = $"{(char)('a' + from % 8)}{8 - from / 8}{(char)('a' + to % 8)}{8 - to / 8}"
                   + PromotionSuffix(last);

        return new PlayedMove(uci, Clean(sanToken), from, to);
    }

    /// <summary>Encodes the promotion the engine actually performed, for the UCI string.</summary>
    private static string PromotionSuffix(MoveContent last) => last.PawnPromotedTo switch
    {
        ChessPieceType.Queen => "q",
        ChessPieceType.Rook => "r",
        ChessPieceType.Bishop => "b",
        ChessPieceType.Knight => "n",
        _ => string.Empty,
    };

    /// <summary>Drops the annotation marks SAN allows to trail a move.</summary>
    private static string Clean(string token) => token.Trim().TrimEnd('+', '#', '!', '?');

}
