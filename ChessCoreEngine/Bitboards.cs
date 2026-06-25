using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ChessEngine.Engine
{
    // Bitboard attack tables and helpers for the new (bitboard) move generator.
    //
    // Square indexing matches the rest of the engine: index 0 = a8 (top-left,
    // Black's queenside rook), index 63 = h1. So for a square index `sq`:
    //   file = sq % 8   (0 = a-file .. 7 = h-file)
    //   row  = sq / 8   (0 = rank 8 (top) .. 7 = rank 1 (bottom))
    // Bit `sq` of a ulong represents that square. "North" (toward rank 8) means
    // decreasing index by 8; "south" (toward rank 1) means increasing by 8;
    // east = +1 (toward h), west = -1.
    //
    // Slider attacks use the classical ray method (precomputed direction rays +
    // a bitscan to the nearest blocker). This is fully portable — no magic
    // constants, no BMI2/PEXT (the dev target is arm64) — and is validated by
    // perft. Magic bitboards are a possible later optimization, not a correctness
    // requirement.
    internal static class Bitboards
    {
        // Single-bit mask for a square.
        internal static readonly ulong[] Bit = new ulong[64];

        // Leaper attack tables (independent of occupancy).
        internal static readonly ulong[] KnightAttacks = new ulong[64];
        internal static readonly ulong[] KingAttacks = new ulong[64];
        // Pawn *capture* targets, indexed [color][square]: 0 = White, 1 = Black.
        internal static readonly ulong[][] PawnAttacks = { new ulong[64], new ulong[64] };

        // The 8 ray directions, indexed by Dir.* below. Ray[dir][sq] is the set of
        // squares strictly beyond `sq` in that direction, out to the board edge.
        internal static readonly ulong[][] Ray = new ulong[8][];

        // Direction indices. Positive = ray walks toward higher square indices
        // (used with TrailingZeroCount to find the nearest blocker); negative =
        // toward lower indices (used with LeadingZeroCount).
        private const int North = 0; // -8  (toward rank 8)
        private const int South = 1; // +8  (toward rank 1)
        private const int East = 2;  // +1
        private const int West = 3;  // -1
        private const int NorthE = 4; // -7
        private const int NorthW = 5; // -9
        private const int SouthE = 6; // +9
        private const int SouthW = 7; // +7

        // Directions whose ray walks toward higher indices (South, East, SouthE, SouthW).
        private static readonly bool[] DirGoesPositive =
        {
            false, true, true, false, false, false, true, true
        };

        static Bitboards()
        {
            for (int sq = 0; sq < 64; sq++)
                Bit[sq] = 1UL << sq;

            for (int d = 0; d < 8; d++)
                Ray[d] = new ulong[64];

            for (int sq = 0; sq < 64; sq++)
            {
                int file = sq % 8;
                int row = sq / 8; // 0 = rank 8 .. 7 = rank 1

                BuildLeapers(sq, file, row);
                BuildRays(sq, file, row);
            }

            // Rays are built; now generate the magic-bitboard slider tables from them.
            InitMagics();
        }

        private static void BuildLeapers(int sq, int file, int row)
        {
            // Knight: all 8 (file,row) offsets, bounds-checked.
            int[][] knightDeltas =
            {
                new[] { 1, 2 }, new[] { 2, 1 }, new[] { 2, -1 }, new[] { 1, -2 },
                new[] { -1, -2 }, new[] { -2, -1 }, new[] { -2, 1 }, new[] { -1, 2 }
            };
            foreach (var d in knightDeltas)
            {
                int f = file + d[0];
                int r = row + d[1];
                if (f >= 0 && f < 8 && r >= 0 && r < 8)
                    KnightAttacks[sq] |= 1UL << (r * 8 + f);
            }

            // King: all 8 surrounding squares.
            for (int df = -1; df <= 1; df++)
            for (int dr = -1; dr <= 1; dr++)
            {
                if (df == 0 && dr == 0) continue;
                int f = file + df;
                int r = row + dr;
                if (f >= 0 && f < 8 && r >= 0 && r < 8)
                    KingAttacks[sq] |= 1UL << (r * 8 + f);
            }

            // Pawn captures. White pawns attack toward rank 8 (row - 1); Black
            // pawns toward rank 1 (row + 1). Diagonals only.
            if (row - 1 >= 0)
            {
                if (file - 1 >= 0) PawnAttacks[0][sq] |= 1UL << ((row - 1) * 8 + file - 1);
                if (file + 1 < 8) PawnAttacks[0][sq] |= 1UL << ((row - 1) * 8 + file + 1);
            }
            if (row + 1 < 8)
            {
                if (file - 1 >= 0) PawnAttacks[1][sq] |= 1UL << ((row + 1) * 8 + file - 1);
                if (file + 1 < 8) PawnAttacks[1][sq] |= 1UL << ((row + 1) * 8 + file + 1);
            }
        }

        private static void BuildRays(int sq, int file, int row)
        {
            // (df, dr) per direction, in (file, row) space. row increases downward.
            int[][] deltas =
            {
                new[] { 0, -1 }, // North
                new[] { 0, 1 },  // South
                new[] { 1, 0 },  // East
                new[] { -1, 0 }, // West
                new[] { 1, -1 }, // NorthE
                new[] { -1, -1 },// NorthW
                new[] { 1, 1 },  // SouthE
                new[] { -1, 1 }  // SouthW
            };

            for (int d = 0; d < 8; d++)
            {
                int f = file + deltas[d][0];
                int r = row + deltas[d][1];
                ulong ray = 0UL;
                while (f >= 0 && f < 8 && r >= 0 && r < 8)
                {
                    ray |= 1UL << (r * 8 + f);
                    f += deltas[d][0];
                    r += deltas[d][1];
                }
                Ray[d][sq] = ray;
            }
        }

        // BitOperations is .NET 5+ only; netstandard2.1 (Unity) needs portable
        // fallbacks. The fallbacks are O(1) bit tricks, not loops, so the Unity
        // build keeps the same throughput.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PopCount(ulong b)
        {
#if NETSTANDARD
            b -= (b >> 1) & 0x5555555555555555UL;
            b = (b & 0x3333333333333333UL) + ((b >> 2) & 0x3333333333333333UL);
            b = (b + (b >> 4)) & 0x0f0f0f0f0f0f0f0fUL;
            return (int)((b * 0x0101010101010101UL) >> 56);
#else
            return BitOperations.PopCount(b);
#endif
        }

        // Index of the least-significant set bit (lowest square index).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Lsb(ulong b)
        {
#if NETSTANDARD
            // trailing-zero count = popcount of the all-ones mask below the lowest set bit
            return PopCount((b & (~b + 1UL)) - 1UL);
#else
            return BitOperations.TrailingZeroCount(b);
#endif
        }

        // Index of the most-significant set bit (highest square index).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Msb(ulong b)
        {
#if NETSTANDARD
            // smear bits down so b = 2^(msb+1)-1, then msb = popcount-1
            b |= b >> 1; b |= b >> 2; b |= b >> 4; b |= b >> 8; b |= b >> 16; b |= b >> 32;
            return PopCount(b) - 1;
#else
            return 63 - BitOperations.LeadingZeroCount(b);
#endif
        }

        // Pop and return the least-significant set bit's index, clearing it in `b`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PopLsb(ref ulong b)
        {
            int i = Lsb(b);
            b &= b - 1;
            return i;
        }

        // Attacks along a single ray, stopping at (and including) the first blocker.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RayAttacks(int dir, int sq, ulong occ)
        {
            ulong ray = Ray[dir][sq];
            ulong blockers = ray & occ;
            if (blockers == 0)
                return ray;

            // Nearest blocker: lowest index if the ray walks toward higher indices,
            // else highest index. Everything strictly beyond it is cut off.
            int blocker = DirGoesPositive[dir] ? Lsb(blockers) : Msb(blockers);
            return ray ^ Ray[dir][blocker];
        }

        // --- Slider attacks via magic bitboards ---
        // Magics are generated at startup (InitMagics) against the masks below, rather
        // than using published constants, because this engine uses an a8=0 square
        // layout. The classical ray attacks are kept as the reference used to fill the
        // lookup tables; at runtime a slider attack is a single table lookup.

        private static ulong ClassicalBishopAttacks(int sq, ulong occ)
        {
            return RayAttacks(NorthE, sq, occ) | RayAttacks(NorthW, sq, occ)
                 | RayAttacks(SouthE, sq, occ) | RayAttacks(SouthW, sq, occ);
        }

        private static ulong ClassicalRookAttacks(int sq, ulong occ)
        {
            return RayAttacks(North, sq, occ) | RayAttacks(South, sq, occ)
                 | RayAttacks(East, sq, occ) | RayAttacks(West, sq, occ);
        }

        private static readonly ulong[] RookMask = new ulong[64];
        private static readonly ulong[] BishopMask = new ulong[64];
        private static readonly int[] RookShift = new int[64];
        private static readonly int[] BishopShift = new int[64];
        private static readonly ulong[][] RookTable = new ulong[64][];
        private static readonly ulong[][] BishopTable = new ulong[64][];

        // Precomputed magic multipliers (generated once with a fixed seed against the
        // masks below; baked in so startup is a fast table-fill, not a search). The
        // fill in InitMagics asserts they're collision-free, so a bad constant fails
        // loudly rather than silently corrupting move generation.
        private static readonly ulong[] RookMagic =
        {
            0x2180028820104002UL, 0x8140001001A00040UL, 0xA1801000A0018008UL, 0x0880100082040800UL, 0x1100040300100800UL, 0x0280020080040001UL, 0x0080008001000200UL, 0xC680006480124100UL,
            0x0020800040002088UL, 0x0123004005002080UL, 0x1001001840200100UL, 0x1001001000200900UL, 0x0808800800040080UL, 0x0000808004000200UL, 0x1000800200800100UL, 0x1002000402208041UL,
            0x088001400040600AUL, 0x0400808020004000UL, 0x2200808020001004UL, 0x0200828008001000UL, 0x2184808004020800UL, 0x0800808002000400UL, 0x0046030100020004UL, 0x0480020008810064UL,
            0x4000400080208000UL, 0x0000400040201000UL, 0xD010080120040120UL, 0x0250008180102801UL, 0x0020040080080080UL, 0x0404008080020004UL, 0x2008040101000200UL, 0x0000410A00004084UL,
            0x0100804004800024UL, 0x0010082008400540UL, 0x0008802008801000UL, 0x0008008008801000UL, 0x0206002006000850UL, 0x0020800400800201UL, 0x4000800100800200UL, 0x1000806402000081UL,
            0x0000208040008002UL, 0x0000201000404000UL, 0x0020081100410020UL, 0x2042100942020020UL, 0x0282002030060028UL, 0x5285000204010008UL, 0x0180084102040070UL, 0x0080041080420001UL,
            0x00800040046004C0UL, 0x0009018022460A00UL, 0x0004200104481100UL, 0x8210040008004040UL, 0x0091000800100500UL, 0x0290040002008080UL, 0x1040021008010400UL, 0x000012A944010200UL,
            0x0008108200A2C102UL, 0x2509008020104001UL, 0x0020024900203141UL, 0x0004100008200501UL, 0x000600A820100512UL, 0x4001000214000803UL, 0x204090084200A114UL, 0x1004040080403102UL
        };
        private static readonly ulong[] BishopMagic =
        {
            0x00405400C0890100UL, 0x0420128082118004UL, 0x2008880100200802UL, 0x00981609C0800001UL, 0x0141104001001010UL, 0x892110281C220610UL, 0x0012220120081080UL, 0x08C0820900824000UL,
            0x2004200224480880UL, 0xC20002C401040100UL, 0x0581A2080D002810UL, 0x4002482042400801UL, 0x0000440308040042UL, 0x0200010402404080UL, 0x40000A01102A3000UL, 0x4000020056480420UL,
            0x4190044004284080UL, 0x0010000811011420UL, 0x0822000404021200UL, 0x0201000820420150UL, 0x8203810401A02300UL, 0x0010808410008800UL, 0x0000880C02184224UL, 0xC006001910908400UL,
            0x0010400012820200UL, 0x0014041610310804UL, 0x0810480004002400UL, 0x0000802002020200UL, 0x0004082004002004UL, 0x2811004008080820UL, 0x220128400C020820UL, 0x0004002400920120UL,
            0x481A205002149116UL, 0x0C220220A0102109UL, 0x00A9080200204080UL, 0x0060200800290105UL, 0x0004104010040100UL, 0x1190300082204040UL, 0x028808104240AA00UL, 0x00080D2108222081UL,
            0x8010822012402000UL, 0x0000441044004800UL, 0x0E00108401001006UL, 0x100A011144020800UL, 0x0002080104020042UL, 0x610410040040280CUL, 0x41220C041404408CUL, 0x100408221020104BUL,
            0x4201209004602888UL, 0x8020440208030000UL, 0x444A021201040008UL, 0xB008000210440210UL, 0x04414440228A1001UL, 0x0001302108012000UL, 0x02882004009A0040UL, 0x0002440122020088UL,
            0x0C20404050101080UL, 0x200140824D101108UL, 0x0400404024020801UL, 0x20C800050020A800UL, 0x0800400020204101UL, 0x0000080450220200UL, 0x0000410401540100UL, 0x0005D00202040010UL
        };

        // (file,row) deltas matching BuildRays' direction set.
        private static readonly int[][] RookDirs = { new[] { 0, -1 }, new[] { 0, 1 }, new[] { 1, 0 }, new[] { -1, 0 } };
        private static readonly int[][] BishopDirs = { new[] { 1, -1 }, new[] { -1, -1 }, new[] { 1, 1 }, new[] { -1, 1 } };

        // Relevant-occupancy mask: ray squares excluding the board-edge square of each
        // ray (a blocker on the edge can't change which squares are attacked).
        private static ulong SliderMask(int sq, int[][] dirs)
        {
            int file = sq % 8, row = sq / 8;
            ulong mask = 0;
            foreach (var d in dirs)
            {
                int f = file + d[0], r = row + d[1];
                while (f >= 0 && f < 8 && r >= 0 && r < 8)
                {
                    int nf = f + d[0], nr = r + d[1];
                    if (nf >= 0 && nf < 8 && nr >= 0 && nr < 8) mask |= 1UL << (r * 8 + f);
                    f = nf; r = nr;
                }
            }
            return mask;
        }

        // Fills a square's attack table from its baked magic by enumerating every
        // occupancy subset of the mask (carry-rippler) and computing the classical
        // attack. Throws if the magic isn't collision-free (guards a bad constant).
        private static void FillTable(int sq, bool bishop)
        {
            ulong mask = bishop ? BishopMask[sq] : RookMask[sq];
            ulong magic = bishop ? BishopMagic[sq] : RookMagic[sq];
            int bits = PopCount(mask);
            int shift = 64 - bits;
            var table = new ulong[1 << bits];
            var filled = new bool[1 << bits];

            ulong b = 0;
            do
            {
                ulong attacks = bishop ? ClassicalBishopAttacks(sq, b) : ClassicalRookAttacks(sq, b);
                int idx = (int)((b * magic) >> shift);
                if (filled[idx] && table[idx] != attacks)
                    throw new InvalidOperationException($"Magic collision at square {sq} ({(bishop ? "bishop" : "rook")}).");
                table[idx] = attacks;
                filled[idx] = true;
                b = (b - mask) & mask;
            } while (b != 0);

            if (bishop) { BishopShift[sq] = shift; BishopTable[sq] = table; }
            else { RookShift[sq] = shift; RookTable[sq] = table; }
        }

        private static void InitMagics()
        {
            for (int sq = 0; sq < 64; sq++)
            {
                RookMask[sq] = SliderMask(sq, RookDirs);
                BishopMask[sq] = SliderMask(sq, BishopDirs);
                FillTable(sq, false);
                FillTable(sq, true);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong BishopAttacks(int sq, ulong occ)
        {
            return BishopTable[sq][((occ & BishopMask[sq]) * BishopMagic[sq]) >> BishopShift[sq]];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong RookAttacks(int sq, ulong occ)
        {
            return RookTable[sq][((occ & RookMask[sq]) * RookMagic[sq]) >> RookShift[sq]];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong QueenAttacks(int sq, ulong occ)
        {
            return BishopAttacks(sq, occ) | RookAttacks(sq, occ);
        }
    }
}
