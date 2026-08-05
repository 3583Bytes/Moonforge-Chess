namespace ChessEngine.Engine
{
    // Transposition table — a fixed-size, hash-indexed cache of search results.
    // When the search revisits a position (via transposition, or via iterative
    // deepening's next iteration), the prior search's result can be reused.
    //
    // Two contributions to playing strength:
    //   1. Cutoff reuse: if a prior search of this position at depth ≥ current
    //      depth gave a score that satisfies our alpha/beta window, return it
    //      directly. Saves the entire subtree.
    //   2. Move ordering: even when we can't cut off (depth too shallow, or the
    //      bound type doesn't match), the prior search's best move is usually
    //      best now too. Trying it first dramatically tightens alpha-beta.
    //
    // Mate scores deliberately NOT stored — they encode "mate in N from THIS
    // position" and a stored mate score would be wrong when retrieved from a
    // different depth. The simple fix is to skip storage/retrieval entirely
    // when the score is in mate range. We lose a small amount of strength on
    // mate problems in exchange for not having to thread plies-from-root
    // through the whole search.
    //
    // Always-replace policy: every store overwrites whatever was there. This is
    // the simplest scheme and works fine at typical bench/match search depths
    // — depth-preferred replacement is a refinement worth ~10–20 Elo on top.
    internal static class TranspositionTable
    {
        internal const byte FlagExact = 1;
        internal const byte FlagLower = 2;   // score is a lower bound (we cut off on beta)
        internal const byte FlagUpper = 3;   // score is an upper bound (no move beat alpha)

        // Mate scores are ±(1,000,000 - plies-to-mate); treat anything outside
        // ±30000 as a mate (or as the ±infinity root bound) and skip.
        internal const int MateThreshold = 30000;

        internal struct Entry
        {
            internal ulong Key;
            internal int Score;
            internal byte Depth;
            internal byte Flag;
            internal byte SrcPosition;
            internal byte DstPosition;
            internal ChessPieceType Promotion;
        }

        // 2^20 entries × ~24 bytes per struct ≈ 24 MB. Bumping the exponent is
        // free behaviourally — search just gets fewer collisions.
        private const int IndexBits = 20;
        private const int Size = 1 << IndexBits;
        private const ulong Mask = Size - 1UL;

        private static readonly Entry[] Table = new Entry[Size];

        internal static void Clear()
        {
            System.Array.Clear(Table, 0, Size);
        }

        // Returns true if a usable cutoff was found; out param `score` is the
        // value to return from the caller. The bestMove fields are set whenever
        // an entry matched, even if no cutoff — caller uses them for ordering.
        internal static bool Probe(ulong key, byte depth, int alpha, int beta,
            out int score, out byte bestSrc, out byte bestDst,
            out ChessPieceType promotion)
        {
            ref Entry e = ref Table[key & Mask];
            score = 0;
            bestSrc = 0;
            bestDst = 0;
            promotion = ChessPieceType.None;

            if (e.Key != key || e.Flag == 0)
                return false;

            // Only use the stored move for ordering if it came from a search
            // deep enough to be informative. A depth-1 move hint is often
            // worse than the engine's static move scoring (captures + killers)
            // and tends to drag the alpha-beta tree wider.
            if (e.Depth >= 2)
            {
                bestSrc = e.SrcPosition;
                bestDst = e.DstPosition;
                promotion = e.Promotion;
            }

            if (e.Depth < depth)
                return false;

            // Entries with mate scores were never stored (see comment above);
            // this guard is belt-and-suspenders against future entries.
            if (e.Score > MateThreshold || e.Score < -MateThreshold)
                return false;

            int s = e.Score;
            switch (e.Flag)
            {
                case FlagExact:
                    score = s;
                    return true;
                case FlagLower:
                    if (s >= beta) { score = beta; return true; }
                    return false;
                case FlagUpper:
                    if (s <= alpha) { score = alpha; return true; }
                    return false;
            }
            return false;
        }

        // PV reconstruction needs depth-1 moves too, while normal move ordering
        // deliberately ignores such shallow hints. This returns only a move;
        // score/bound validation remains the responsibility of Probe.
        internal static bool TryGetMove(ulong key, int minimumDepth,
            out byte source, out byte destination, out ChessPieceType promotion)
        {
            ref Entry e = ref Table[key & Mask];
            source = 0;
            destination = 0;
            promotion = ChessPieceType.None;

            if (e.Key != key || e.Flag == 0 || e.Depth < minimumDepth
                || (e.SrcPosition == 0 && e.DstPosition == 0))
                return false;

            source = e.SrcPosition;
            destination = e.DstPosition;
            promotion = e.Promotion;
            return true;
        }

        internal static void Store(ulong key, int score, byte depth, byte flag,
            byte bestSrc, byte bestDst, ChessPieceType promotion)
        {
            if (score > MateThreshold || score < -MateThreshold)
                return;

            ref Entry e = ref Table[key & Mask];

            // Depth-preferred replacement. A shallow result is much less useful
            // than a deep one, so we keep the deeper entry — except when:
            //   * the slot is empty (Flag == 0), or
            //   * we're updating the exact same position (Key == key) — the
            //     newer search at this depth is at least as informative.
            // Without this guard a flood of depth-1 stores (e.g. from the
            // root-level mate-check loop in IterativeSearch) wipes out the
            // deep cuts and bench regresses.
            if (e.Flag != 0 && e.Key != key && e.Depth > depth)
                return;

            e.Key = key;
            e.Score = score;
            e.Depth = depth;
            e.Flag = flag;
            e.SrcPosition = bestSrc;
            e.DstPosition = bestDst;
            e.Promotion = promotion;
        }
    }
}
