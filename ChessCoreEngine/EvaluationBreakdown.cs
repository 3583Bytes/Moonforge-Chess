namespace ChessEngine.Engine
{
    /// <summary>
    /// Named contributions to Moonforge's static evaluation. Values are in
    /// centipawns from White's point of view: positive favors White.
    /// </summary>
    public struct EvaluationBreakdown
    {
        public int Material { get; internal set; }
        public int PieceSquareTables { get; internal set; }
        public int Mobility { get; internal set; }
        public int AttackDefense { get; internal set; }
        public int PawnStructure { get; internal set; }
        public int KingSafety { get; internal set; }
        public int MinorPieceAdjustments { get; internal set; }
        public int QueenDevelopment { get; internal set; }
        public int Check { get; internal set; }
        public int Castling { get; internal set; }
        public int Tempo { get; internal set; }
        public int DrawAdjustment { get; internal set; }
        public string DrawReason { get; internal set; }

        public int Total => Material
                          + PieceSquareTables
                          + Mobility
                          + AttackDefense
                          + PawnStructure
                          + KingSafety
                          + MinorPieceAdjustments
                          + QueenDevelopment
                          + Check
                          + Castling
                          + Tempo
                          + DrawAdjustment;
    }
}
