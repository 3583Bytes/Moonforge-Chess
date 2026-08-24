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

    /// <summary>SAN: piece, optional disambiguation, optional capture, target, optional promotion.</summary>
    private static readonly Regex San = new(
        @"^(?<piece>[KQRBN])?(?<ff>[a-h])?(?<fr>[1-8])?(?<cap>x)?(?<tf>[a-h])(?<tr>[1-8])(?:=?(?<promo>[QRBN]))?$",
        RegexOptions.Compiled);

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
        string san = Clean(sanToken);
        if (san.Length == 0) return null;

        ChessPieceColor mover = engine.WhoseMove;

        if (IsCastle(san, out bool kingSide))
        {
            int row = mover == ChessPieceColor.White ? 7 : 0;
            int kingTo = kingSide ? 6 : 2;
            // Engine.MovePiece applies whatever it is handed without checking legality, so the
            // generator is the gate here as it is for every other move.
            return CanReach(engine, 4, row, kingTo, row)
                ? TryMove(engine, 4, row, kingTo, row, ChessPieceType.Queen, sanToken)
                : null;
        }

        Match m = San.Match(san);
        if (!m.Success) return null;

        int toColumn = m.Groups["tf"].Value[0] - 'a';
        int toRow = 8 - (m.Groups["tr"].Value[0] - '0');
        ChessPieceType piece = m.Groups["piece"].Success ? PieceFrom(m.Groups["piece"].Value[0]) : ChessPieceType.Pawn;
        ChessPieceType promo = m.Groups["promo"].Success ? PieceFrom(m.Groups["promo"].Value[0]) : ChessPieceType.Queen;
        int? fromFile = m.Groups["ff"].Success ? m.Groups["ff"].Value[0] - 'a' : null;
        int? fromRank = m.Groups["fr"].Success ? 8 - (m.Groups["fr"].Value[0] - '0') : null;

        // Find every piece of that type which can legally reach the target, then apply the
        // notation's disambiguation hints. Valid PGN leaves exactly one.
        var candidates = new List<(int Column, int Row)>();
        for (byte column = 0; column < 8; column++)
        {
            for (byte row = 0; row < 8; row++)
            {
                if (engine.GetPieceTypeAt(column, row) != piece) continue;
                if (engine.GetPieceColorAt(column, row) != mover) continue;
                if (fromFile is int ff && column != ff) continue;
                if (fromRank is int fr && row != fr) continue;
                if (!CanReach(engine, column, row, toColumn, toRow)) continue;
                candidates.Add((column, row));
            }
        }

        // The mailbox generator can offer a move that leaves the king in check, so let the
        // engine reject it and fall through to the next candidate rather than trusting the list.
        foreach ((int column, int row) in candidates)
        {
            PlayedMove? applied = TryMove(engine, column, row, toColumn, toRow, promo, sanToken);
            if (applied is not null) return applied;
        }

        return null;
    }

    private static bool CanReach(Engine engine, int column, int row, int toColumn, int toRow)
    {
        byte[][]? targets = engine.GetValidMoves((byte)column, (byte)row);
        if (targets is null) return false;
        foreach (byte[] t in targets)
        {
            if (t[0] == toColumn && t[1] == toRow) return true;
        }
        return false;
    }

    private static PlayedMove? TryMove(
        Engine engine, int fromColumn, int fromRow, int toColumn, int toRow, ChessPieceType promotion, string label)
    {
        engine.PromoteToPieceType = promotion;
        if (!engine.MovePiece((byte)fromColumn, (byte)fromRow, (byte)toColumn, (byte)toRow)) return null;

        MoveContent last = engine.LastMove;
        string uci = $"{(char)('a' + fromColumn)}{8 - fromRow}{(char)('a' + toColumn)}{8 - toRow}"
                   + PromotionSuffix(promotion, toRow, last);

        return new PlayedMove(uci, Clean(label), last.MovingPiecePrimary.SrcPosition, last.MovingPiecePrimary.DstPosition);
    }

    private static string PromotionSuffix(ChessPieceType promotion, int toRow, MoveContent last) =>
        toRow is not (0 or 7) || last.PawnPromotedTo == ChessPieceType.None
            ? string.Empty
            : promotion switch
            {
                ChessPieceType.Queen => "q",
                ChessPieceType.Rook => "r",
                ChessPieceType.Bishop => "b",
                ChessPieceType.Knight => "n",
                _ => string.Empty,
            };

    /// <summary>Drops the annotation marks SAN allows to trail a move.</summary>
    private static string Clean(string token) => token.Trim().TrimEnd('+', '#', '!', '?');

    private static bool IsCastle(string san, out bool kingSide)
    {
        string s = san.Replace('0', 'O').Replace("--", "-");
        kingSide = s is "O-O" or "OO";
        return kingSide || s is "O-O-O" or "OOO";
    }

    private static ChessPieceType PieceFrom(char c) => c switch
    {
        'K' => ChessPieceType.King,
        'Q' => ChessPieceType.Queen,
        'R' => ChessPieceType.Rook,
        'B' => ChessPieceType.Bishop,
        'N' => ChessPieceType.Knight,
        _ => ChessPieceType.Pawn,
    };
}
