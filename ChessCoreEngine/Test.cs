using System;

namespace ChessEngine.Engine
{
    // Perft now runs on the bitboard core (Engine.RunPerformanceTest → MoveGen.Perft).
    // Only the result type is retained here, as Engine.RunPerformanceTest's return value.
    public class PerformanceTest
    {
        public struct PerformanceResult
        {
            public int Depth;
            public long Nodes;
            public TimeSpan TimeSpan;
        }
    }
}
