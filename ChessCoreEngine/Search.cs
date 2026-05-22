using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace ChessEngine.Engine
{
    internal static class Search
    {
        internal static int progress;

		private static int piecesRemaining;

        // Time-abort plumbing. _searchClock starts when IterativeSearch begins; _deadlineTicks
        // is the Stopwatch.GetTimestamp() value beyond which the next abort check trips
        // _aborted. _deadlineTicks == 0 disables the deadline entirely (used by `go depth N`
        // and bench, which want fixed-depth behavior).
        //
        // We poll every _abortCheckInterval node visits rather than every node — Stopwatch
        // reads are ~20ns each, but at ~200kn/s that's still measurable overhead, and the
        // abort can tolerate a few ms of latency.
        private const int _abortCheckInterval = 2048;
        private static long _deadlineTicks;
        private static long _searchStartTicks;
        private static bool _aborted;
        private static int _abortCheckCounter;

        private static bool CheckAbort()
        {
            if (_aborted) return true;
            if (_deadlineTicks == 0) return false;
            if (++_abortCheckCounter < _abortCheckInterval) return false;
            _abortCheckCounter = 0;
            if (Stopwatch.GetTimestamp() >= _deadlineTicks)
            {
                _aborted = true;
                return true;
            }
            return false;
        }

        private struct Position
        {
            internal byte SrcPosition;
            internal byte DstPosition;
            internal int Score;
            //internal bool TopSort;
            internal string Move;

            public new string ToString()
            {
                return Move;
            }

        }

        private static readonly Position[,] KillerMove = new Position[3,20];
        private static int kIndex;

        // History heuristic: per-(color, from, to) score bumped on quiet beta cutoffs.
        // EvaluateMoves reads it to seed quiet-move ordering, so moves that have caused
        // cutoffs at this or higher depths float to the front of the list. Only quiet
        // moves contribute and consume — captures are already ordered by MVV-LVA.
        //
        // Reset at the start of every IterativeSearch so old games' patterns don't pollute
        // a new search. Across ID iterations within one search the table accumulates —
        // cutoffs from depth d help order moves at depth d+1.
        //
        // Bonus is depth² so deeper cutoffs (which represent more search work) dominate
        // shallow ones. Capped at HistoryMax to keep the value comparable to capture
        // scores (queen value ~900; we cap below so history can re-order quiets without
        // leapfrogging real captures).
        private const int HistoryMax = 512;
        private static readonly int[,,] _history = new int[2, 64, 64];

        private static int Sort(Position s2, Position s1)
        {
            return (s1.Score).CompareTo(s2.Score);
        }

        private static int Sort(Board s2, Board s1)
        {
            return (s1.Score).CompareTo(s2.Score);
        }

        private static int SideToMoveScore(int score, ChessPieceColor color)
        {
            if (color == ChessPieceColor.Black)
                return -score;

            return score;
        }

       

        // Iterative deepening with optional wall-clock abort.
        //
        // `maxDepth` is the hard ceiling. `deadlineMs` is the soft cap: when > 0, the
        // search stops the next time a node check trips after `deadlineMs` of wall time.
        // Partial iterations are discarded — we always return the best move from the
        // last *completed* depth. With deadlineMs == 0 (e.g. `go depth N`, bench, training
        // mode), the search runs to maxDepth deterministically.
        //
        // The big win over the prior fixed-depth search: every speedup we've added (TT,
        // null move, LMR) now translates into more depth within the same budget, rather
        // than being thrown away by a coarse depth bucket.
        internal static MoveContent IterativeSearch(Board examineBoard, byte maxDepth, long deadlineMs, ref int nodesSearched, ref int nodesQuiessence, ref string pvLine, ref byte plyDepthReached, ref byte rootMovesSearched, List<OpeningMove> currentGameBook, out int searchScore)
        {
            // Reset abort state and start the clock.
            _aborted = false;
            _abortCheckCounter = 0;
            _searchStartTicks = Stopwatch.GetTimestamp();
            _deadlineTicks = deadlineMs > 0
                ? _searchStartTicks + (long)(deadlineMs * Stopwatch.Frequency / 1000L)
                : 0;

            // History heuristic table is per-search — wipe state from prior moves so a
            // position's quiet-move ordering is driven by *this* search's cutoffs.
            Array.Clear(_history, 0, _history.Length);

            searchScore = 0;
            plyDepthReached = 0;

            // Build root move list once. GetSortValidMoves filters illegal moves, scores
            // each post-move position via the static eval, and sorts descending — so
            // succ.Positions[0] is the static-eval-best move and a safe fallback if we
            // never complete a single iteration.
            ResultBoards succ = GetSortValidMoves(examineBoard);
            rootMovesSearched = (byte)succ.Positions.Count;

            if (rootMovesSearched == 0)
            {
                // No legal moves — caller (Engine.AiPonderMove) detects mate/stalemate
                // before getting here, but defend anyway.
                return new MoveContent();
            }

            if (rootMovesSearched == 1)
            {
                // Only one legal move; no point searching it.
                searchScore = succ.Positions[0].Score;
                return succ.Positions[0].LastMove;
            }

            // Endgame / low-mobility ceiling boost. Same heuristic as the prior code —
            // when there are few moves or few pieces, push the ceiling up by 1-2 plies
            // because the branching factor is small.
            byte ceiling = ModifyDepth(maxDepth, succ.Positions.Count);

            // Fallback in case the very first iteration is aborted mid-way: the static-eval
            // best move from move ordering. Should be rare in practice (depth 1 finishes
            // in microseconds even on big positions).
            MoveContent bestMove = succ.Positions[0].LastMove;
            int bestScore = succ.Positions[0].Score;
            string bestPv = bestMove.ToString();

            for (byte d = 1; d <= ceiling; d++)
            {
                // Promote the previous iteration's best move to root[0] so its likely TT
                // hit and tight alpha-beta window benefit every subsequent move at this
                // depth. By far the most impactful ordering change at the root.
                if (d > 1)
                {
                    for (int i = 1; i < succ.Positions.Count; i++)
                    {
                        var lm = succ.Positions[i].LastMove.MovingPiecePrimary;
                        if (lm.SrcPosition == bestMove.MovingPiecePrimary.SrcPosition
                            && lm.DstPosition == bestMove.MovingPiecePrimary.DstPosition)
                        {
                            Board tmp = succ.Positions[0];
                            succ.Positions[0] = succ.Positions[i];
                            succ.Positions[i] = tmp;
                            break;
                        }
                    }
                }

                int alpha = -400000000;
                const int beta = 400000000;
                MoveContent iterBest = succ.Positions[0].LastMove;
                int iterBestScore = -400000000;
                string iterPv = iterBest.ToString();
                bool iterCompleted = true;

                for (int i = 0; i < succ.Positions.Count; i++)
                {
                    Board pos = succ.Positions[i];
                    progress = (int)(((i + 1) / (decimal)succ.Positions.Count) * 100);

                    List<Position> pvChild = new List<Position>();
                    // We've already made one move into `pos`, so AlphaBeta searches (d-1)
                    // more plies; total visible depth from the root is d.
                    int value = -AlphaBeta(pos, (byte)(d - 1), -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, false, true);

                    if (_aborted) { iterCompleted = false; break; }

                    // 3-fold avoidance: at RepeatedMove==2 the next visit to a position
                    // already in our game book is a forced draw. Score it as 0 so we
                    // don't blunder into a draw when winning. (Inherited from prior code.)
                    if (examineBoard.RepeatedMove == 2)
                    {
                        string fen = Board.Fen(true, pos);
                        foreach (OpeningMove move in currentGameBook)
                        {
                            if (move.EndingFEN == fen) { value = 0; break; }
                        }
                    }

                    pos.Score = value;

                    if (value > iterBestScore)
                    {
                        iterBestScore = value;
                        iterBest = pos.LastMove;

                        string pv = pos.LastMove.ToString();
                        foreach (Position pvPos in pvChild) pv += " " + pvPos.ToString();
                        iterPv = pv;

                        if (value > alpha) alpha = value;
                    }
                }

                progress = 100;

                // If we aborted partway through an iteration the partial results aren't
                // trustworthy (some moves at this depth weren't searched) — discard and
                // keep the previous iteration's best.
                if (!iterCompleted) break;

                bestMove = iterBest;
                bestScore = iterBestScore;
                bestPv = iterPv;
                plyDepthReached = d;

                // Found a forced mate — searching deeper would only refine the mate distance,
                // and the score encoding (32767+depth) already guarantees the shortest mate
                // is picked. Save time.
                if (bestScore >= 32767) break;
            }

            searchScore = bestScore;
            pvLine = bestPv;
            return bestMove;
        }

        private static ResultBoards GetSortValidMoves(Board examineBoard)
        {
            ResultBoards succ = new ResultBoards
                                    {
                                        Positions = new List<Board>(30)
                                    };

            piecesRemaining = 0;

            for (byte x = 0; x < 64; x++)
            {
                Square sqr = examineBoard.Squares[x];

                //Make sure there is a piece on the square
                if (sqr.Piece == null)
                    continue;

                piecesRemaining++;

                //Make sure the color is the same color as the one we are moving.
                if (sqr.Piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in sqr.Piece.ValidMoves)
                {
                    //We make copies of the board and move so that we can move it without effecting the parent board
                    Board board = examineBoard.FastCopy();

                    //Make move so we can examine it
                    Board.MovePiece(board, x, dst, ChessPieceType.Queen);

                    //We Generate Valid Moves for Board
                    PieceValidMoves.GenerateValidMoves(board);

                    //Invalid Move
                    if (board.WhiteCheck && examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        continue;
                    }

                    //Invalid Move
                    if (board.BlackCheck && examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        continue;
                    }

                    //We calculate the board score
                    Evaluation.EvaluateBoardScore(board);

                    //Invert Score to support Negamax
                    board.Score = SideToMoveScore(board.Score, board.WhoseMove);

                    succ.Positions.Add(board);
                }
            }

            succ.Positions.Sort(Sort);
            return succ;
        }

        private static int AlphaBeta(Board examineBoard, byte depth, int alpha, int beta, ref int nodesSearched, ref int nodesQuiessence, ref List<Position> pvLine, bool extended, bool allowNullMove)
        {
            nodesSearched++;

            // Time abort: bail out of the recursion. The returned value is meaningless —
            // IterativeSearch checks _aborted after this call and discards the partial result.
            if (CheckAbort()) return 0;

            if (examineBoard.HalfMoveClock >= 100 || examineBoard.RepeatedMove >= 3)
                return 0;

            //End Main Search with Quiescence
            if (depth == 0)
            {
                if (!extended && (examineBoard.BlackCheck || examineBoard.WhiteCheck))
                {
                    depth++;
                    extended = true;
                }
                else
                {
                    //Perform a Quiessence Search
                    return Quiescence(examineBoard, alpha, beta, ref nodesQuiessence, 0);
                }
            }

            // Transposition table probe. If we've seen this exact position before
            // at this depth or deeper, the stored bound may already cut us off.
            // Even when it doesn't, the stored best move is captured and tried
            // first below — that's where the bulk of the alpha-beta speedup comes
            // from.
            ulong ttKey = examineBoard.ZobristHash;
            byte ttSrc, ttDst;
            if (TranspositionTable.Probe(ttKey, depth, alpha, beta, out int ttScore, out ttSrc, out ttDst))
                return ttScore;
            int origAlpha = alpha;

            // Null move pruning. Hand the opponent a free move; if even then the
            // search comes back >= beta, the position is too good for us to need
            // to search properly — the opponent can't refute it from here.
            // Skip when:
            //   * in check (a null leaves our king attacked, illegal)
            //   * depth too low for the reduction to pay off
            //   * caller was itself a null (avoids double-null which proves nothing)
            //   * few pieces left (zugzwang: "doing nothing" can genuinely be best
            //     in K+P endgames, so NMP gives wrong cutoffs)
            //   * |beta| near mate (cutoffs against mate scores are unreliable)
            const byte NullR = 2;
            if (allowNullMove
                && depth >= 1 + NullR
                && !(examineBoard.WhiteCheck || examineBoard.BlackCheck)
                && piecesRemaining > 6
                && Math.Abs(beta) < 30000)
            {
                Board nullBoard = examineBoard.FastCopy();
                nullBoard.WhoseMove = examineBoard.WhoseMove == ChessPieceColor.White
                    ? ChessPieceColor.Black
                    : ChessPieceColor.White;
                // The en-passant target was set up by our prior move's
                // pawn-push; if we pass instead, that target is no longer
                // legitimately capturable. Clear it so the null search doesn't
                // see an illegal en-passant capture as legal.
                nullBoard.EnPassantPosition = 0;
                PieceValidMoves.GenerateValidMoves(nullBoard);

                List<Position> nullPv = new List<Position>();
                int nullScore = -AlphaBeta(nullBoard, (byte)(depth - 1 - NullR), -beta, -beta + 1,
                    ref nodesSearched, ref nodesQuiessence, ref nullPv, extended, false);
                if (nullScore >= beta)
                    return beta;
            }

            List<Position> positions = EvaluateMoves(examineBoard, depth);

            if (examineBoard.WhiteCheck || examineBoard.BlackCheck || positions.Count == 0)
            {
                if (SearchForMate(examineBoard.WhoseMove, examineBoard, ref examineBoard.BlackMate, ref examineBoard.WhiteMate, ref examineBoard.StaleMate))
                {
                    if (examineBoard.BlackMate)
                    {
                        if (examineBoard.WhoseMove == ChessPieceColor.Black)
                            return -32767-depth;

                        return 32767 + depth;
                    }
                    if (examineBoard.WhiteMate)
                    {
                        if (examineBoard.WhoseMove == ChessPieceColor.Black)
                            return 32767 + depth;

                        return -32767 - depth;
                    }

                    //If Not Mate then StaleMate
                    return 0;
                }
            }

            positions.Sort(Sort);

            // Promote the TT move (best move from a prior search of this position)
            // to the front. This is the biggest single move-ordering win — the
            // prior search's best move is almost always still best, so trying it
            // first lets alpha-beta cut the rest of the list cheaply.
            if ((ttSrc != 0 || ttDst != 0))
            {
                for (int i = 1; i < positions.Count; i++)
                {
                    if (positions[i].SrcPosition == ttSrc && positions[i].DstPosition == ttDst)
                    {
                        Position tmp = positions[0];
                        positions[0] = positions[i];
                        positions[i] = tmp;
                        break;
                    }
                }
            }

            byte bestSrc = 0, bestDst = 0;
            int legalMoveCount = 0;

            foreach (Position move in positions)
            {
                List<Position> pvChild = new List<Position>();

                //Make a copy
                Board board = examineBoard.FastCopy();

                //Move Piece
                Board.MovePiece(board, move.SrcPosition, move.DstPosition, ChessPieceType.Queen);

                //We Generate Valid Moves for Board
                PieceValidMoves.GenerateValidMoves(board);

                if (board.BlackCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                if (board.WhiteCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                // Late Move Reductions. Move ordering above already floats the
                // tactically interesting moves to the front (TT move, captures,
                // killers). The remaining quiet moves are unlikely to be best
                // — search them at reduced depth, and only re-search at full
                // depth if the reduced search beats alpha.
                //
                // Skip reduction for moves too tactically loaded to gamble on:
                //   * captures (incl. en passant — pawn moving off its file
                //     while the dst square is empty)
                //   * promotions
                //   * we're already in check (every reply is forced)
                //   * the move gives check
                //   * killer move (EvaluateMoves tags these with Score == 5000
                //     and `continue`s past every other bonus, so the equality
                //     check is exact)
                Piece movingPiece = examineBoard.Squares[move.SrcPosition].Piece;
                bool isCapture = examineBoard.Squares[move.DstPosition].Piece != null;
                bool isPawnMove = movingPiece.PieceType == ChessPieceType.Pawn;
                bool isCaptureOrEp = isCapture
                    || (isPawnMove && (move.SrcPosition & 7) != (move.DstPosition & 7));
                bool isPromotion = isPawnMove
                    && (move.DstPosition < 8 || move.DstPosition >= 56);
                bool inCheck = examineBoard.WhiteCheck || examineBoard.BlackCheck;
                bool givesCheck = examineBoard.WhoseMove == ChessPieceColor.White
                    ? board.BlackCheck
                    : board.WhiteCheck;
                bool isKiller = move.Score == 5000;

                int reduction = 0;
                if (depth >= 3
                    && legalMoveCount >= 3
                    && !isCaptureOrEp
                    && !isPromotion
                    && !inCheck
                    && !givesCheck
                    && !isKiller)
                {
                    reduction = legalMoveCount >= 6 ? 2 : 1;
                    if (reduction >= depth) reduction = depth - 1;
                }

                legalMoveCount++;

                int value = -AlphaBeta(board, (byte)(depth - 1 - reduction), -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, extended, true);

                // Verify a reduced search that beat alpha at full depth before
                // trusting it — the reduction is a heuristic, not a proof.
                if (reduction > 0 && value > alpha)
                {
                    pvChild = new List<Position>();
                    value = -AlphaBeta(board, (byte)(depth - 1), -beta, -alpha, ref nodesSearched, ref nodesQuiessence, ref pvChild, extended, true);
                }

                if (value >= beta)
                {
                    KillerMove[kIndex, depth].SrcPosition = move.SrcPosition;
                    KillerMove[kIndex, depth].DstPosition = move.DstPosition;

                    kIndex = ((kIndex + 1) % 2);

                    // History bump: reward quiet moves that caused cutoffs so they get
                    // tried earlier next time we hit a similar position. Captures and
                    // promotions are already ordered well by MVV-LVA + promotion bonus,
                    // so they don't feed the table.
                    if (!isCaptureOrEp && !isPromotion)
                    {
                        int colorIdx = (examineBoard.WhoseMove == ChessPieceColor.White) ? 0 : 1;
                        int bumped = _history[colorIdx, move.SrcPosition, move.DstPosition] + depth * depth;
                        if (bumped > HistoryMax) bumped = HistoryMax;
                        _history[colorIdx, move.SrcPosition, move.DstPosition] = bumped;
                    }

                    // Beta cutoff: score is a lower bound (true value ≥ beta).
                    TranspositionTable.Store(ttKey, beta, depth, TranspositionTable.FlagLower,
                        move.SrcPosition, move.DstPosition);
                    return beta;
                }
                if (value > alpha)
                {
                    Position pvPos = new Position();

                    pvPos.SrcPosition = board.LastMove.MovingPiecePrimary.SrcPosition;
                    pvPos.DstPosition = board.LastMove.MovingPiecePrimary.DstPosition;
                    pvPos.Move = board.LastMove.ToString();

                    pvChild.Insert(0, pvPos);

                    pvLine = pvChild;

                    alpha = (int)value;
                    bestSrc = move.SrcPosition;
                    bestDst = move.DstPosition;
                }
            }

            // Determine bound type and store:
            //   alpha raised → at least one move strictly beat origAlpha → EXACT.
            //   alpha unchanged → every move failed low → UPPER bound on true value.
            byte storeFlag = (alpha > origAlpha) ? TranspositionTable.FlagExact : TranspositionTable.FlagUpper;
            TranspositionTable.Store(ttKey, alpha, depth, storeFlag, bestSrc, bestDst);

            return alpha;
        }

        private static int Quiescence(Board examineBoard, int alpha, int beta, ref int nodesSearched, int qsPly)
        {
            nodesSearched++;

            // Same abort guard as AlphaBeta — qsearch trees can run a long time on
            // capture-heavy positions, so we need a deadline check here too.
            if (CheckAbort()) return 0;

            //Evaluate Score
            Evaluation.EvaluateBoardScore(examineBoard);

            //Invert Score to support Negamax
            examineBoard.Score = SideToMoveScore(examineBoard.Score, examineBoard.WhoseMove);

            if (examineBoard.Score >= beta)
                return beta;

            if (examineBoard.Score > alpha)
                alpha = examineBoard.Score;


            List<Position> positions;
            bool inCheck = examineBoard.WhiteCheck || examineBoard.BlackCheck;
            // At the first quiescence ply also consider non-capture *knight*
            // checks. This catches forking tactics like the Nf3+ pattern from
            // game 1 (knight at e5 -> f3, forks K+R) that captures-only qsearch
            // misses. Bounded to qsPly == 0 so the tree doesn't explode, and
            // bounded to knights so we can pre-filter cheaply with the
            // precomputed KnightMoves table — full slider-check detection
            // needs blocker walks and isn't worth the per-node cost yet.
            bool includeChecks = qsPly == 0 && !inCheck;

            byte enemyKingPos = examineBoard.WhoseMove == ChessPieceColor.White
                ? examineBoard.BlackKingPosition
                : examineBoard.WhiteKingPosition;

            if (inCheck)
            {
                positions = EvaluateMoves(examineBoard, 0);
            }
            else if (includeChecks)
            {
                positions = EvaluateMovesQPlusKnightChecks(examineBoard, enemyKingPos);
            }
            else
            {
                positions = EvaluateMovesQ(examineBoard);
            }

            if (positions.Count == 0)
            {
                return examineBoard.Score;
            }

            positions.Sort(Sort);

            foreach (Position move in positions)
            {
                bool isCapture = examineBoard.Squares[move.DstPosition].Piece != null;

                // Skip captures that look like material losses; keep equal/winning ones.
                // Non-captures emitted by the generators (inCheck evasions, knight
                // checks at qsPly==0) bypass SEE.
                if (isCapture && StaticExchangeEvaluation(examineBoard.Squares[move.DstPosition]) < 0)
                {
                    continue;
                }

                //Make a copy
                Board board = examineBoard.FastCopy();

                //Move Piece
                Board.MovePiece(board, move.SrcPosition, move.DstPosition, ChessPieceType.Queen);

                //We Generate Valid Moves for Board
                PieceValidMoves.GenerateValidMoves(board);

                if (board.BlackCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                if (board.WhiteCheck)
                {
                    if (examineBoard.WhoseMove == ChessPieceColor.White)
                    {
                        //Invalid Move
                        continue;
                    }
                }

                int value = -Quiescence(board, -beta, -alpha, ref nodesSearched, qsPly + 1);

                if (value >= beta)
                {
                    KillerMove[2, 0].SrcPosition = move.SrcPosition;
                    KillerMove[2, 0].DstPosition = move.DstPosition;

                    return beta;
                }
                if (value > alpha)
                {
                    alpha = value;
                }
            }

            return alpha;
        }

        private static List<Position> EvaluateMoves(Board examineBoard, byte depth)
        {

            //We are going to store our result boards here           
            List<Position> positions = new List<Position>();

            //bool foundPV = false;


            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;

                //Make sure there is a piece on the square
                if (piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in piece.ValidMoves)
                {
                    Position move = new Position();

                    move.SrcPosition = x;
                    move.DstPosition = dst;
				
                    if (move.SrcPosition == KillerMove[0, depth].SrcPosition && move.DstPosition == KillerMove[0, depth].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }
                    if (move.SrcPosition == KillerMove[1, depth].SrcPosition && move.DstPosition == KillerMove[1, depth].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    Piece pieceAttacked = examineBoard.Squares[move.DstPosition].Piece;

                    //If the move is a capture add it's value to the score
                    if (pieceAttacked != null)
                    {
                        move.Score += pieceAttacked.PieceValue;

                        if (piece.PieceValue < pieceAttacked.PieceValue)
                        {
                            move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                        }
                    }

                    if (!piece.Moved)
                    {
                        move.Score += 10;
                    }

                    move.Score += piece.PieceActionValue;

                    //Add Score for Castling
                    if (!examineBoard.WhiteCastled && examineBoard.WhoseMove == ChessPieceColor.White)
                    {

                        if (piece.PieceType == ChessPieceType.King)
                        {
                            if (move.DstPosition != 62 && move.DstPosition != 58)
                            {
                                move.Score -= 40;
                            }
                            else
                            {
                                move.Score += 40;
                            }
                        }
                        if (piece.PieceType == ChessPieceType.Rook)
                        {
                            move.Score -= 40;
                        }
                    }

                    if (!examineBoard.BlackCastled && examineBoard.WhoseMove == ChessPieceColor.Black)
                    {
                        if (piece.PieceType == ChessPieceType.King)
                        {
                            if (move.DstPosition != 6 && move.DstPosition != 2)
                            {
                                move.Score -= 40;
                            }
                            else
                            {
                                move.Score += 40;
                            }
                        }
                        if (piece.PieceType == ChessPieceType.Rook)
                        {
                            move.Score -= 40;
                        }
                    }

                    // History: nudge quiet moves toward the front of the list when they've
                    // caused beta cutoffs at earlier ID iterations. Captures already have
                    // strong MVV-LVA ordering; history would just add noise to them.
                    if (pieceAttacked == null)
                    {
                        int colorIdx = (examineBoard.WhoseMove == ChessPieceColor.White) ? 0 : 1;
                        move.Score += _history[colorIdx, move.SrcPosition, move.DstPosition];
                    }

                    positions.Add(move);
                }
            }

            return positions;
        }

        // Captures + non-capture knight checks (squares from which a knight
        // would attack enemyKingPos). Used at the first quiescence ply when
        // not already in check, so the tree picks up tactical knight forks
        // (the Game 1 Nf3+ pattern) without paying the full EvaluateMoves
        // cost. Sliders/pawn checks omitted — too expensive to detect cheaply.
        private static List<Position> EvaluateMovesQPlusKnightChecks(Board examineBoard, byte enemyKingPos)
        {
            List<Position> positions = new List<Position>();
            List<byte> knightCheckSquares = MoveArrays.KnightMoves[enemyKingPos].Moves;
            ChessPieceColor color = examineBoard.WhoseMove;

            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;
                if (piece == null) continue;
                if (piece.PieceColor != color) continue;

                bool isKnight = piece.PieceType == ChessPieceType.Knight;

                foreach (byte dst in piece.ValidMoves)
                {
                    Piece pieceAttacked = examineBoard.Squares[dst].Piece;
                    bool isCapture = pieceAttacked != null;

                    if (!isCapture)
                    {
                        // Non-capture: only emit if it's a knight giving check.
                        if (!isKnight) continue;
                        if (!knightCheckSquares.Contains(dst)) continue;
                    }

                    Position move = new Position();
                    move.SrcPosition = x;
                    move.DstPosition = dst;

                    if (move.SrcPosition == KillerMove[2, 0].SrcPosition && move.DstPosition == KillerMove[2, 0].DstPosition)
                    {
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    if (isCapture)
                    {
                        move.Score += pieceAttacked.PieceValue;
                        if (piece.PieceValue < pieceAttacked.PieceValue)
                        {
                            move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                        }
                    }

                    move.Score += piece.PieceActionValue;
                    positions.Add(move);
                }
            }

            return positions;
        }

        private static List<Position> EvaluateMovesQ(Board examineBoard)
        {
            //We are going to store our result boards here           
            List<Position> positions = new List<Position>();

            for (byte x = 0; x < 64; x++)
            {
                Piece piece = examineBoard.Squares[x].Piece;

                //Make sure there is a piece on the square
                if (piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (piece.PieceColor != examineBoard.WhoseMove)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in piece.ValidMoves)
                {
                    if (examineBoard.Squares[dst].Piece == null)
                    {
                        continue;
                    }

                    Position move = new Position();

                    move.SrcPosition = x;
                    move.DstPosition = dst;

                    if (move.SrcPosition == KillerMove[2, 0].SrcPosition && move.DstPosition == KillerMove[2, 0].DstPosition)
                    {
                        //move.TopSort = true;
                        move.Score += 5000;
                        positions.Add(move);
                        continue;
                    }

                    Piece pieceAttacked = examineBoard.Squares[move.DstPosition].Piece;

                    move.Score += pieceAttacked.PieceValue;

                    if (piece.PieceValue < pieceAttacked.PieceValue)
                    {
                        move.Score += pieceAttacked.PieceValue - piece.PieceValue;
                    }

                    move.Score += piece.PieceActionValue;


                    positions.Add(move);
                }
            }

            return positions;
        }

        internal static bool SearchForMate(ChessPieceColor movingSide, Board examineBoard, ref bool blackMate, ref bool whiteMate, ref bool staleMate)
        {
            bool foundNonCheckBlack = false;
            bool foundNonCheckWhite = false;

            for (byte x = 0; x < 64; x++)
            {
                Square sqr = examineBoard.Squares[x];

                //Make sure there is a piece on the square
                if (sqr.Piece == null)
                    continue;

                //Make sure the color is the same color as the one we are moving.
                if (sqr.Piece.PieceColor != movingSide)
                    continue;

                //For each valid move for this piece
                foreach (byte dst in sqr.Piece.ValidMoves)
                {

                    //We make copies of the board and move so that we can move it without effecting the parent board
                    Board board = examineBoard.FastCopy();

                    //Make move so we can examine it
                    Board.MovePiece(board, x, dst, ChessPieceType.Queen);

                    //We Generate Valid Moves for Board
                    PieceValidMoves.GenerateValidMoves(board);

                    if (board.BlackCheck == false)
                    {
                        foundNonCheckBlack = true;
                    }
                    else if (movingSide == ChessPieceColor.Black)
                    {
                        continue;
                    }

                    if (board.WhiteCheck == false )
                    {
                        foundNonCheckWhite = true;
                    }
                    else if (movingSide == ChessPieceColor.White)
                    {
                        continue;
                    }
                }
            }

            if (foundNonCheckBlack == false)
            {
                if (examineBoard.BlackCheck)
                {
                    blackMate = true;
                    return true;
                }
                if (!examineBoard.WhiteMate && movingSide != ChessPieceColor.White)
                {
                    staleMate = true;
                    return true;
                }
            }

            if (foundNonCheckWhite == false)
            {
                if (examineBoard.WhiteCheck)
                {
                    whiteMate = true;
                    return true;
                }
                if (!examineBoard.BlackMate && movingSide != ChessPieceColor.Black)
                {
                    staleMate = true;
                    return true;
                }
            }

            return false;
           
        }

        private static byte ModifyDepth(byte depth, int possibleMoves)
        {
            if (possibleMoves <= 20 || piecesRemaining < 14)
            {
                if (possibleMoves <= 10 || piecesRemaining < 6)
                {
                    depth += 1;
                }

                depth += 1;
            }

            return depth;
        }

        private static int StaticExchangeEvaluation(Square examineSquare)
        {
            if (examineSquare.Piece == null)
            {
                return 0;
            }
            if (examineSquare.Piece.AttackedValue == 0)
            {
                return 0;
            }

            return examineSquare.Piece.PieceActionValue - examineSquare.Piece.AttackedValue + examineSquare.Piece.DefendedValue;
        }

    }
}
