namespace ChessEngine.Engine
{
    // Native (bitboard-only) reproduction of the per-piece evaluation SIGNALS that
    // PieceValidMoves.GenerateValidMoves produces as side-effects:
    //   * Attacked[sq] / Defended[sq] — summed PieceActionValue of enemy / friendly
    //     attackers of an occupied square (sliders occupancy-aware; the enemy-king
    //     square still accrues Attacked).
    //   * Mobility[sq] — the legacy per-piece valid-move count, including its quirks
    //     (enemy-king square not counted; king moves exclude enemy-attacked squares;
    //     castling counted).
    //   * WhiteAtt/BlackAtt — per-color attack maps.
    //
    // Validated square-by-square against GenerateValidMoves (see EvalSignalsTests).
    // Once exact, BitboardEvalNative.Score evaluates a Position with no Board / no
    // GenerateValidMoves on the hot path.
    internal static class BitboardEvalNative
    {
        // PieceActionValue by ChessPieceType (King=0..Pawn=5), per Piece.CalculatePieceActionValue.
        private static readonly int[] ActionValue = { 1, 1, 2, 3, 3, 6 };

        internal struct Signals
        {
            internal short[] Attacked;
            internal short[] Defended;
            internal int[] Mobility;
            internal bool[] WhiteAtt;
            internal bool[] BlackAtt;
            internal bool WhiteCheck;
            internal bool BlackCheck;
            internal bool EndGame;
        }

        internal static Signals ComputeSignals(Position p)
        {
            var s = new Signals
            {
                Attacked = new short[64],
                Defended = new short[64],
                Mobility = new int[64],
                WhiteAtt = new bool[64],
                BlackAtt = new bool[64],
                EndGame = Bitboards.PopCount(p.OccAll) < 10
            };

            // Phase 1: every non-king piece of both colors. Builds the attack maps,
            // accrues attack/defend values, and sets mobility for non-pawns.
            for (int color = 0; color < 2; color++)
            {
                ulong own = p.OccByColor[color];
                int enemyKingSq = p.KingSq[color ^ 1];
                bool[] attMap = color == 0 ? s.WhiteAtt : s.BlackAtt;
                int baseIdx = color * 6;

                AddPieceType(p, s, attMap, own, enemyKingSq, baseIdx, ChessPieceType.Knight, color);
                AddPieceType(p, s, attMap, own, enemyKingSq, baseIdx, ChessPieceType.Bishop, color);
                AddPieceType(p, s, attMap, own, enemyKingSq, baseIdx, ChessPieceType.Rook, color);
                AddPieceType(p, s, attMap, own, enemyKingSq, baseIdx, ChessPieceType.Queen, color);
                AddPawns(p, s, attMap, color);
            }

            // Check flags (GenerateValidMoves sets BlackCheck when White attacks the
            // black king, and vice versa).
            s.BlackCheck = MoveGen.IsSquareAttacked(p, p.KingSq[1], 0);
            s.WhiteCheck = MoveGen.IsSquareAttacked(p, p.KingSq[0], 1);

            // Phase 2: kings, in GenerateValidMoves' order (the side NOT to move is
            // processed first), so the second king sees the first king's attack map.
            if (p.SideToMove == 0)
            {
                AddKing(p, s, 1);
                AddKing(p, s, 0);
            }
            else
            {
                AddKing(p, s, 0);
                AddKing(p, s, 1);
            }

            return s;
        }

        private static void AddPieceType(Position p, Signals s, bool[] attMap, ulong own,
                                          int enemyKingSq, int baseIdx, ChessPieceType type, int color)
        {
            int av = ActionValue[(int)type];
            ulong bb = p.Pieces[baseIdx + (int)type];
            while (bb != 0)
            {
                int from = Bitboards.PopLsb(ref bb);
                ulong attacks = type switch
                {
                    ChessPieceType.Knight => Bitboards.KnightAttacks[from],
                    ChessPieceType.Bishop => Bitboards.BishopAttacks(from, p.OccAll),
                    ChessPieceType.Rook => Bitboards.RookAttacks(from, p.OccAll),
                    _ => Bitboards.QueenAttacks(from, p.OccAll)
                };

                AccrueAttacks(p, s, attMap, attacks, color, av);

                // Mobility: empty or enemy-occupied targets, minus the enemy king
                // square (attacking the king isn't a move in the legacy generator).
                int mob = Bitboards.PopCount(attacks & ~own);
                if ((attacks & Bitboards.Bit[enemyKingSq]) != 0) mob--;
                s.Mobility[from] = mob;
            }
        }

        private static void AddPawns(Position p, Signals s, bool[] attMap, int color)
        {
            int av = ActionValue[(int)ChessPieceType.Pawn];
            ulong pawns = p.Pieces[color * 6 + (int)ChessPieceType.Pawn];
            int forward = color == 0 ? -8 : 8;
            ulong startRank = color == 0 ? 0x00FF000000000000UL : 0x000000000000FF00UL;
            int enemyKingSq = p.KingSq[color ^ 1];
            ulong enemy = p.OccByColor[color ^ 1];

            while (pawns != 0)
            {
                int from = Bitboards.PopLsb(ref pawns);
                ulong diag = Bitboards.PawnAttacks[color][from];

                // Diagonal attacks: mark the map and accrue attack/defend on occupied squares.
                AccrueAttacks(p, s, attMap, diag, color, av);

                int mob = 0;
                // Forward pushes.
                int one = from + forward;
                if (one >= 0 && one < 64 && p.PieceOn[one] == Position.EMPTY)
                {
                    mob++;
                    if ((Bitboards.Bit[from] & startRank) != 0 && p.PieceOn[one + forward] == Position.EMPTY)
                        mob++;
                }
                // Diagonal captures of enemy non-king pieces.
                ulong caps = diag & enemy & ~Bitboards.Bit[enemyKingSq];
                mob += Bitboards.PopCount(caps);
                // En passant — only the side to move can capture (the side that just
                // double-pushed cannot capture its own en-passant target).
                if (color == p.SideToMove && p.EpSquare != -1 && (diag & Bitboards.Bit[p.EpSquare]) != 0) mob++;

                s.Mobility[from] = mob;
            }
        }

        private static void AddKing(Position p, Signals s, int color)
        {
            int ks = p.KingSq[color];
            int av = ActionValue[(int)ChessPieceType.King];
            ulong own = p.OccByColor[color];
            bool[] ownMap = color == 0 ? s.WhiteAtt : s.BlackAtt;
            bool[] enemyMap = color == 0 ? s.BlackAtt : s.WhiteAtt;

            ulong moves = Bitboards.KingAttacks[ks];
            int mob = 0;
            ulong bb = moves;
            while (bb != 0)
            {
                int d = Bitboards.PopLsb(ref bb);
                ownMap[d] = true; // king marks its full ring on the attack map
                if (enemyMap[d]) continue; // can't move into an attacked square (no value, no mobility)

                int code = p.PieceOn[d];
                if (code == Position.EMPTY) { mob++; continue; }
                if (code / 6 == color) s.Defended[d] += (short)av;     // defends own piece
                else { s.Attacked[d] += (short)av; mob++; }            // attacks enemy piece (a move)
            }

            mob += CastleMobility(p, s, color);
            s.Mobility[ks] = mob;
        }

        // Counts available castling moves for `color`, replicating
        // GenerateValidMovesKingCastle (gated by not-castled / has-right / not-in-check).
        private static int CastleMobility(Position p, Signals s, int color)
        {
            int count = 0;
            if (color == 0)
            {
                bool canCastle = (p.CastleRights & (Position.WK | Position.WQ)) != 0;
                if (p.Castled[0] || !canCastle || s.WhiteCheck) return 0;
                // Kingside: rook on h1, f1/g1 empty, f1/g1 not black-attacked.
                if (p.PieceOn[63] == Position.PieceIndex(ChessPieceColor.White, ChessPieceType.Rook)
                    && p.PieceOn[62] == Position.EMPTY && p.PieceOn[61] == Position.EMPTY
                    && !s.BlackAtt[61] && !s.BlackAtt[62]) count++;
                // Queenside: rook on a1, b1/c1/d1 empty, c1/d1 not black-attacked.
                if (p.PieceOn[56] == Position.PieceIndex(ChessPieceColor.White, ChessPieceType.Rook)
                    && p.PieceOn[57] == Position.EMPTY && p.PieceOn[58] == Position.EMPTY && p.PieceOn[59] == Position.EMPTY
                    && !s.BlackAtt[58] && !s.BlackAtt[59]) count++;
            }
            else
            {
                bool canCastle = (p.CastleRights & (Position.BK | Position.BQ)) != 0;
                if (p.Castled[1] || !canCastle || s.BlackCheck) return 0;
                // GenerateValidMoves' black castle gen also checks the rook hasn't Moved;
                // a rook on its home square with the matching right has Moved == false.
                if (p.PieceOn[7] == Position.PieceIndex(ChessPieceColor.Black, ChessPieceType.Rook)
                    && (p.CastleRights & Position.BK) != 0
                    && p.PieceOn[6] == Position.EMPTY && p.PieceOn[5] == Position.EMPTY
                    && !s.WhiteAtt[5] && !s.WhiteAtt[6]) count++;
                if (p.PieceOn[0] == Position.PieceIndex(ChessPieceColor.Black, ChessPieceType.Rook)
                    && (p.CastleRights & Position.BQ) != 0
                    && p.PieceOn[1] == Position.EMPTY && p.PieceOn[2] == Position.EMPTY && p.PieceOn[3] == Position.EMPTY
                    && !s.WhiteAtt[2] && !s.WhiteAtt[3]) count++;
            }
            return count;
        }

        // PieceValue by type (King=0..Pawn=5), per Piece.CalculatePieceValue.
        private static readonly int[] Material = { 32767, 975, 500, 325, 320, 100 };

        // Native evaluation: reproduces Evaluation.EvaluateBoardScore exactly from the
        // Position + computed signals, with no Board projection and no
        // GenerateValidMoves. Validated against BitboardEval.ScoreViaLegacyGen.
        internal static int Score(Position p) => Evaluate(p).Total;

        internal static EvaluationBreakdown DetailedScore(Position p) => Evaluate(p);

        private static EvaluationBreakdown Evaluate(Position p)
        {
            var result = new EvaluationBreakdown();
            if (p.HalfmoveClock >= 100)
            {
                result.DrawReason = "50-move rule";
                return result;
            }

            Signals sig = ComputeSignals(p);
            bool endGame = sig.EndGame;

            if (sig.BlackCheck) { result.Check += 70; if (endGame) result.Check += 10; }
            else if (sig.WhiteCheck) { result.Check -= 70; if (endGame) result.Check -= 10; }
            if (p.Castled[1]) result.Castling -= 50;
            if (p.Castled[0]) result.Castling += 50;
            result.Tempo = p.SideToMove == 0 ? 10 : -10;

            var whitePawnCount = new short[8];
            var blackPawnCount = new short[8];
            int whiteKnight = 0, whiteBishop = 0, blackKnight = 0, blackBishop = 0;
            bool insufficient = true;

            for (int sq = 0; sq < 64; sq++)
            {
                int code = p.PieceOn[sq];
                if (code == Position.EMPTY) continue;
                int color = code / 6;
                int type = code % 6;
                int index = color == 0 ? sq : sq ^ 56; // rank mirror for Black
                int sign = color == 0 ? 1 : -1;

                result.Material += sign * Material[type];

                int attackDefense = sig.Defended[sq] - sig.Attacked[sq];
                if (sig.Defended[sq] < sig.Attacked[sq])
                    attackDefense -= (sig.Attacked[sq] - sig.Defended[sq]) * 10;
                result.AttackDefense += sign * attackDefense;
                result.Mobility += sign * sig.Mobility[sq];

                switch ((ChessPieceType)type)
                {
                    case ChessPieceType.Pawn:
                        insufficient = false;
                        int file = sq % 8;
                        if (file == 0 || file == 7) result.PawnStructure -= sign * 15;
                        result.PieceSquareTables += sign * Evaluation.PawnTable[index];
                        result.PawnStructure += sign * PawnChainBonus(p, sq, color);
                        if (color == 0)
                        {
                            if (whitePawnCount[file] > 0) result.PawnStructure -= 15;
                            if (sq >= 8 && sq <= 15) { if (sig.Attacked[sq] == 0) { whitePawnCount[file] += 100; if (sig.Defended[sq] != 0) whitePawnCount[file] += 50; } }
                            else if (sq >= 16 && sq <= 23) { if (sig.Attacked[sq] == 0) { whitePawnCount[file] += 50; if (sig.Defended[sq] != 0) whitePawnCount[file] += 25; } }
                            whitePawnCount[file] += 10;
                        }
                        else
                        {
                            if (blackPawnCount[file] > 0) result.PawnStructure += 15;
                            if (sq >= 48 && sq <= 55) { if (sig.Attacked[sq] == 0) { blackPawnCount[file] += 100; if (sig.Defended[sq] != 0) blackPawnCount[file] += 50; } }
                            else if (sq >= 40 && sq <= 47) { if (sig.Attacked[sq] == 0) { blackPawnCount[file] += 50; if (sig.Defended[sq] != 0) blackPawnCount[file] += 25; } }
                            blackPawnCount[file] += 10;
                        }
                        break;
                    case ChessPieceType.Knight:
                        if (color == 0) whiteKnight++; else blackKnight++;
                        result.PieceSquareTables += sign * Evaluation.KnightTable[index];
                        if (endGame) result.MinorPieceAdjustments -= sign * 10;
                        break;
                    case ChessPieceType.Bishop:
                        int bc = color == 0 ? ++whiteBishop : ++blackBishop;
                        if (bc == 2) result.MinorPieceAdjustments += sign * 10;
                        if (endGame) result.MinorPieceAdjustments += sign * 10;
                        result.PieceSquareTables += sign * Evaluation.BishopTable[index];
                        break;
                    case ChessPieceType.Rook:
                        insufficient = false;
                        break;
                    case ChessPieceType.Queen:
                        insufficient = false;
                        if (!endGame) result.QueenDevelopment -= sign * 10; // queen.Moved is always true in this engine
                        break;
                    case ChessPieceType.King:
                        if (sig.Mobility[sq] < 2) result.KingSafety -= sign * 5;
                        result.PieceSquareTables += sign *
                            (endGame ? Evaluation.KingTableEndGame[index] : Evaluation.KingTable[index]);
                        break;
                }

                if (type == (int)ChessPieceType.King)
                {
                    if (color == 0 && sq != 60)
                        result.KingSafety += CheckPawnWall(p, sq - 8, sq) + CheckPawnWall(p, sq - 7, sq) + CheckPawnWall(p, sq - 9, sq);
                    else if (color == 1 && sq != 4)
                        result.KingSafety -= CheckPawnWall(p, sq + 8, sq) + CheckPawnWall(p, sq + 7, sq) + CheckPawnWall(p, sq + 9, sq);
                }
            }

            if (insufficient && (whiteBishop + whiteKnight > 1 || blackBishop + blackKnight > 1))
                insufficient = false;
            if (insufficient)
            {
                result.DrawReason = "insufficient material";
                result.DrawAdjustment = -result.Total;
                return result;
            }

            if (!endGame)
            {
                bool whiteCanCastle = (p.CastleRights & (Position.WK | Position.WQ)) != 0;
                bool blackCanCastle = (p.CastleRights & (Position.BK | Position.BQ)) != 0;
                if (!whiteCanCastle && !p.Castled[0]) result.Castling -= 50;
                if (!blackCanCastle && !p.Castled[1]) result.Castling += 50;

                result.KingSafety += KingFileOpenness(p.KingSq[0], 0, whitePawnCount, blackPawnCount)
                                   + KingZoneAttacks(sig.BlackAtt, p.KingSq[0]);
                result.KingSafety -= KingFileOpenness(p.KingSq[1], 1, whitePawnCount, blackPawnCount)
                                   + KingZoneAttacks(sig.WhiteAtt, p.KingSq[1]);
            }

            result.PawnStructure += PawnStructure(whitePawnCount, blackPawnCount);
            return result;
        }

        private static int PawnChainBonus(Position p, int sq, int color)
        {
            int file = sq % 8;
            int back = color == 0 ? -8 : 8;
            int left = sq + back - 1, right = sq + back + 1;
            int pawnCode = color * 6 + (int)ChessPieceType.Pawn;
            int s = 0;
            if (file > 0 && left >= 0 && left < 64 && p.PieceOn[left] == pawnCode) s += 5;
            if (file < 7 && right >= 0 && right < 64 && p.PieceOn[right] == pawnCode) s += 5;
            return s;
        }

        private static int CheckPawnWall(Position p, int pawnPos, int kingPos)
        {
            if (kingPos % 8 == 7 && pawnPos % 8 == 0) return 0;
            if (kingPos % 8 == 0 && pawnPos % 8 == 7) return 0;
            if (pawnPos > 63 || pawnPos < 0) return 0;
            int kingColor = p.PieceOn[kingPos] / 6;
            return p.PieceOn[pawnPos] == kingColor * 6 + (int)ChessPieceType.Pawn ? 10 : 0;
        }

        private static int KingFileOpenness(int kingPos, int color, short[] wpc, short[] bpc)
        {
            int score = 0;
            int kingFile = kingPos % 8;
            for (int dFile = -1; dFile <= 1; dFile++)
            {
                int file = kingFile + dFile;
                if (file < 0 || file > 7) continue;
                int our = color == 0 ? wpc[file] : bpc[file];
                int their = color == 0 ? bpc[file] : wpc[file];
                if (our == 0)
                {
                    score -= dFile == 0 ? 22 : 14;
                    if (their == 0) score -= 10;
                }
            }
            return score;
        }

        private static readonly short[] KingZonePenalty = { 0, -4, -12, -24, -40, -60, -80, -100 };

        private static int KingZoneAttacks(bool[] enemyAtt, int kingPos)
        {
            int kf = kingPos % 8, kr = kingPos / 8, attacked = 0;
            for (int dr = -1; dr <= 1; dr++)
                for (int df = -1; df <= 1; df++)
                {
                    if (dr == 0 && df == 0) continue;
                    int f = kf + df, r = kr + dr;
                    if (f < 0 || f > 7 || r < 0 || r > 7) continue;
                    if (enemyAtt[r * 8 + f]) attacked++;
                }
            return KingZonePenalty[attacked > 7 ? 7 : attacked];
        }

        // Isolated- and passed-pawn terms, transcribed from EvaluateBoardScore.
        private static int PawnStructure(short[] w, short[] b)
        {
            int s = 0;
            // Black isolated (good for White → +)
            if (b[0] >= 1 && b[1] == 0) s += 12;
            if (b[1] >= 1 && b[0] == 0 && b[2] == 0) s += 14;
            if (b[2] >= 1 && b[1] == 0 && b[3] == 0) s += 16;
            if (b[3] >= 1 && b[2] == 0 && b[4] == 0) s += 20;
            if (b[4] >= 1 && b[3] == 0 && b[5] == 0) s += 20;
            if (b[5] >= 1 && b[4] == 0 && b[6] == 0) s += 16;
            if (b[6] >= 1 && b[5] == 0 && b[7] == 0) s += 14;
            if (b[7] >= 1 && b[6] == 0) s += 12;
            // White isolated (bad for White → −)
            if (w[0] >= 1 && w[1] == 0) s -= 12;
            if (w[1] >= 1 && w[0] == 0 && w[2] == 0) s -= 14;
            if (w[2] >= 1 && w[1] == 0 && w[3] == 0) s -= 16;
            if (w[3] >= 1 && w[2] == 0 && w[4] == 0) s -= 20;
            if (w[4] >= 1 && w[3] == 0 && w[5] == 0) s -= 20;
            if (w[5] >= 1 && w[4] == 0 && w[6] == 0) s -= 16;
            if (w[6] >= 1 && w[5] == 0 && w[7] == 0) s -= 14;
            if (w[7] >= 1 && w[6] == 0) s -= 12;
            // Black passed (good for Black → −)
            if (b[0] >= 1 && w[0] == 0 && w[1] == 0) s -= b[0];
            if (b[1] >= 1 && w[0] == 0 && w[1] == 0 && w[2] == 0) s -= b[1];
            if (b[2] >= 1 && w[1] == 0 && w[2] == 0 && w[3] == 0) s -= b[2];
            if (b[3] >= 1 && w[2] == 0 && w[3] == 0 && w[4] == 0) s -= b[3];
            if (b[4] >= 1 && w[3] == 0 && w[4] == 0 && w[5] == 0) s -= b[4];
            if (b[5] >= 1 && w[4] == 0 && w[5] == 0 && w[6] == 0) s -= b[5];
            if (b[6] >= 1 && w[5] == 0 && w[6] == 0 && w[7] == 0) s -= b[6];
            if (b[7] >= 1 && w[6] == 0 && w[7] == 0) s -= b[7];
            // White passed (good for White → +)
            if (w[0] >= 1 && b[0] == 0 && b[1] == 0) s += w[0];
            if (w[1] >= 1 && b[0] == 0 && b[1] == 0 && b[2] == 0) s += w[1];
            if (w[2] >= 1 && b[1] == 0 && b[2] == 0 && b[3] == 0) s += w[2];
            if (w[3] >= 1 && b[2] == 0 && b[3] == 0 && b[4] == 0) s += w[3];
            if (w[4] >= 1 && b[3] == 0 && b[4] == 0 && b[5] == 0) s += w[4];
            if (w[5] >= 1 && b[4] == 0 && b[5] == 0 && b[6] == 0) s += w[5];
            if (w[6] >= 1 && b[5] == 0 && b[6] == 0 && b[7] == 0) s += w[6];
            if (w[7] >= 1 && b[6] == 0 && b[7] == 0) s += w[7];
            return s;
        }

        private static void AccrueAttacks(Position p, Signals s, bool[] attMap, ulong attacks, int color, int av)
        {
            ulong bb = attacks;
            while (bb != 0)
            {
                int d = Bitboards.PopLsb(ref bb);
                attMap[d] = true;
                int code = p.PieceOn[d];
                if (code == Position.EMPTY) continue;
                if (code / 6 == color) s.Defended[d] += (short)av;
                else s.Attacked[d] += (short)av;
            }
        }
    }
}
