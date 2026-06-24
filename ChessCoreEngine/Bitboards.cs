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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PopCount(ulong b) => BitOperations.PopCount(b);

        // Index of the least-significant set bit (lowest square index).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Lsb(ulong b) => BitOperations.TrailingZeroCount(b);

        // Index of the most-significant set bit (highest square index).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Msb(ulong b) => 63 - BitOperations.LeadingZeroCount(b);

        // Pop and return the least-significant set bit's index, clearing it in `b`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int PopLsb(ref ulong b)
        {
            int i = BitOperations.TrailingZeroCount(b);
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

        internal static ulong BishopAttacks(int sq, ulong occ)
        {
            return RayAttacks(NorthE, sq, occ) | RayAttacks(NorthW, sq, occ)
                 | RayAttacks(SouthE, sq, occ) | RayAttacks(SouthW, sq, occ);
        }

        internal static ulong RookAttacks(int sq, ulong occ)
        {
            return RayAttacks(North, sq, occ) | RayAttacks(South, sq, occ)
                 | RayAttacks(East, sq, occ) | RayAttacks(West, sq, occ);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong QueenAttacks(int sq, ulong occ)
        {
            return BishopAttacks(sq, occ) | RookAttacks(sq, occ);
        }
    }
}
