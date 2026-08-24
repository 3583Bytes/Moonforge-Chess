using System;
using System.Text.RegularExpressions;

namespace ChessEngine.Engine
{
    /// <summary>
    /// Resolves standard algebraic notation against a live position and applies it.
    /// <para>
    /// <see cref="PGN"/> has always been able to write SAN; this reads it back. Resolution
    /// works by asking the generator which pieces of the named type can legally reach the
    /// target square, then applying the notation's disambiguation hints — so a move is only
    /// ever applied if the generator produced it.
    /// </para>
    /// </summary>
    public static class SanMove
    {
        private static readonly Regex Pattern = new Regex(
            @"^(?<piece>[KQRBN])?(?<ff>[a-h])?(?<fr>[1-8])?(?<cap>x)?(?<tf>[a-h])(?<tr>[1-8])(?:=?(?<promo>[QRBN]))?$",
            RegexOptions.Compiled);

        /// <summary>
        /// Applies one SAN move, returning false if it cannot be read or is not legal here.
        /// Check and annotation marks are ignored; "0-0" is accepted alongside "O-O".
        /// </summary>
        public static bool TryApply(Engine engine, string san)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            if (string.IsNullOrWhiteSpace(san)) return false;

            string move = Clean(san);
            if (move.Length == 0) return false;

            ChessPieceColor mover = engine.WhoseMove;

            bool kingSide;
            if (IsCastle(move, out kingSide))
            {
                int row = mover == ChessPieceColor.White ? 7 : 0;
                int kingTo = kingSide ? 6 : 2;
                return CanReach(engine, 4, row, kingTo, row)
                    && Apply(engine, 4, row, kingTo, row, ChessPieceType.Queen);
            }

            Match m = Pattern.Match(move);
            if (!m.Success) return false;

            int toColumn = m.Groups["tf"].Value[0] - 'a';
            int toRow = 8 - (m.Groups["tr"].Value[0] - '0');
            ChessPieceType piece = m.Groups["piece"].Success ? PieceFrom(m.Groups["piece"].Value[0]) : ChessPieceType.Pawn;
            ChessPieceType promotion = m.Groups["promo"].Success ? PieceFrom(m.Groups["promo"].Value[0]) : ChessPieceType.Queen;
            int fromFile = m.Groups["ff"].Success ? m.Groups["ff"].Value[0] - 'a' : -1;
            int fromRank = m.Groups["fr"].Success ? 8 - (m.Groups["fr"].Value[0] - '0') : -1;

            for (byte column = 0; column < 8; column++)
            {
                for (byte row = 0; row < 8; row++)
                {
                    if (engine.GetPieceTypeAt(column, row) != piece) continue;
                    if (engine.GetPieceColorAt(column, row) != mover) continue;
                    if (fromFile >= 0 && column != fromFile) continue;
                    if (fromRank >= 0 && row != fromRank) continue;
                    if (!CanReach(engine, column, row, toColumn, toRow)) continue;

                    if (Apply(engine, column, row, toColumn, toRow, promotion)) return true;
                }
            }

            return false;
        }

        /// <summary>Whether a SAN move is legal in this position, leaving the board untouched.</summary>
        public static bool IsLegal(string fen, string san)
        {
            if (string.IsNullOrWhiteSpace(fen)) return false;

            Engine probe;
            try
            {
                probe = new Engine(fen);
                probe.GenerateValidMoves();
            }
            catch (Exception)
            {
                return false;
            }

            return TryApply(probe, san);
        }

        private static bool Apply(Engine engine, int fromColumn, int fromRow, int toColumn, int toRow, ChessPieceType promotion)
        {
            engine.PromoteToPieceType = promotion;
            return engine.MovePiece((byte)fromColumn, (byte)fromRow, (byte)toColumn, (byte)toRow);
        }

        private static bool CanReach(Engine engine, int column, int row, int toColumn, int toRow)
        {
            byte[][] targets = engine.GetValidMoves((byte)column, (byte)row);
            if (targets == null) return false;

            foreach (byte[] target in targets)
            {
                if (target[0] == toColumn && target[1] == toRow) return true;
            }

            return false;
        }

        /// <summary>Drops the annotation marks SAN allows to trail a move.</summary>
        private static string Clean(string token) => token.Trim().TrimEnd('+', '#', '!', '?');

        private static bool IsCastle(string san, out bool kingSide)
        {
            string s = san.Replace('0', 'O').Replace("--", "-");
            kingSide = s == "O-O" || s == "OO";
            return kingSide || s == "O-O-O" || s == "OOO";
        }

        private static ChessPieceType PieceFrom(char c)
        {
            switch (c)
            {
                case 'K': return ChessPieceType.King;
                case 'Q': return ChessPieceType.Queen;
                case 'R': return ChessPieceType.Rook;
                case 'B': return ChessPieceType.Bishop;
                case 'N': return ChessPieceType.Knight;
                default: return ChessPieceType.Pawn;
            }
        }
    }
}
