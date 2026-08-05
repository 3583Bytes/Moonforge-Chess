using System;

namespace ChessEngine.Engine
{
    /// <summary>One fully completed iteration of Moonforge's search.</summary>
    public sealed class EngineSearchInfo
    {
        public const int MateScore = 1_000_000;
        private const int MateRange = 1_000;

        public int Depth { get; internal set; }
        public int Score { get; internal set; }
        public long Nodes { get; internal set; }
        public long QuiescenceNodes { get; internal set; }
        public string PrincipalVariation { get; internal set; } = string.Empty;

        public long TotalNodes => Nodes + QuiescenceNodes;
        public bool IsMate
        {
            get
            {
                int absolute = Math.Abs(Score);
                return absolute >= MateScore - MateRange && absolute <= MateScore;
            }
        }

        /// <summary>
        /// UCI-style signed moves to mate. Positive means the side to move can
        /// force mate; negative means it is being mated.
        /// </summary>
        public int MateInMoves
        {
            get
            {
                if (!IsMate) return 0;
                int plies = Math.Max(0, MateScore - Math.Abs(Score));
                int moves = (plies + 1) / 2;
                return Score < 0 ? -moves : moves;
            }
        }
    }

    /// <summary>The non-mutating result of searching the current position.</summary>
    public sealed class EngineSearchResult
    {
        public bool HasMove { get; internal set; }
        public bool FromBook { get; internal set; }
        public string BestMove { get; internal set; } = string.Empty;
        public EngineSearchInfo Info { get; internal set; } = new EngineSearchInfo();
    }
}
