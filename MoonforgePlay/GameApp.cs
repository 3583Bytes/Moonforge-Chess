using ChessEngine.Engine;
using Spectre.Console;

namespace MoonforgePlay;

internal sealed class GameApp
{
    private readonly Engine _engine;
    private readonly BoardRenderer _renderer;

    public GameApp()
    {
        _engine = new Engine();
        _engine.HumanPlayer = ChessPieceColor.White;
        _engine.GameDifficulty = Engine.Difficulty.Medium;
        _engine.GenerateValidMoves();
        _renderer = new BoardRenderer(_engine);
    }

    public void Run()
    {
        _renderer.RenderAll();

        while (true)
        {
            if (CheckGameOverAndAnnounce()) break;

            var ev = InputReader.Read();
            bool needsRender = false;

            switch (ev.Kind)
            {
                case InputKind.Escape:
                    return;

                case InputKind.MouseClick:
                    needsRender = HandleClick(ev.MouseCol, ev.MouseRow);
                    break;

                case InputKind.Char:
                    if (AcceptChar(ev.Char))
                    {
                        needsRender = true;
                    }
                    break;

                case InputKind.Backspace:
                    if (_renderer.TypedBuffer.Length > 0)
                    {
                        _renderer.TypedBuffer = _renderer.TypedBuffer[..^1];
                        needsRender = true;
                    }
                    break;

                case InputKind.Enter:
                    needsRender = TryApplyTyped();
                    break;
            }

            if (needsRender) _renderer.RenderDynamic();
        }

        // Game over — wait for any key to dismiss.
        InputReader.Read();
    }

    private bool HandleClick(int col, int row)
    {
        if (!_renderer.TryHitTest(col, row, out byte file, out byte rankTop))
        {
            return false;
        }

        // Click on the engine's clock during its turn does nothing.
        if (_engine.WhoseMove != _engine.HumanPlayer)
        {
            _renderer.Status = "Engine is thinking…";
            return true;
        }

        var clickedType = _engine.GetPieceTypeAt(file, rankTop);
        var clickedColor = _engine.GetPieceColorAt(file, rankTop);

        if (_renderer.Selected is { } sel)
        {
            // Second click. If it's a legal destination, move.
            if (_renderer.LegalDests.Contains((file, rankTop)))
            {
                bool needPromo = IsPromotionMove(sel.col, sel.row, file, rankTop);
                if (needPromo)
                {
                    var promo = AskPromotion();
                    _engine.PromoteToPieceType = promo;
                }
                else
                {
                    _engine.PromoteToPieceType = ChessPieceType.Queen;
                }

                ClearSelection();

                if (TryMove(sel.col, sel.row, file, rankTop))
                {
                    EngineReply();
                }
                return true;
            }

            // Second click on another own piece → reselect.
            if (clickedType != ChessPieceType.None && clickedColor == _engine.HumanPlayer)
            {
                SelectSquare(file, rankTop);
                return true;
            }

            // Anything else → clear selection.
            ClearSelection();
            _renderer.Status = "";
            return true;
        }
        else
        {
            // First click. Must land on own piece to start a selection.
            if (clickedType != ChessPieceType.None && clickedColor == _engine.HumanPlayer)
            {
                SelectSquare(file, rankTop);
                return true;
            }
            _renderer.Status = "Click one of your pieces to begin.";
            return true;
        }
    }

    private void SelectSquare(byte file, byte rankTop)
    {
        _renderer.Selected = (file, rankTop);
        _renderer.LegalDests.Clear();

        // Filter pseudo-legal moves through IsValidMove for true legality (rejects
        // moves that would leave own king in check).
        var moves = _engine.GetValidMoves(file, rankTop);
        if (moves != null)
        {
            foreach (var m in moves)
            {
                byte dc = m[0], dr = m[1];
                if (_engine.IsValidMove(file, rankTop, dc, dr))
                {
                    _renderer.LegalDests.Add((dc, dr));
                }
            }
        }
        _renderer.Status = $"Selected {Algebraic(file, rankTop)}. Click a highlighted square or press Esc.";
    }

    private void ClearSelection()
    {
        _renderer.Selected = null;
        _renderer.LegalDests.Clear();
    }

    private bool AcceptChar(char c)
    {
        c = char.ToLowerInvariant(c);
        if (_renderer.TypedBuffer.Length >= 5) return false;

        bool keep = _renderer.TypedBuffer.Length switch
        {
            0 or 2 => c is >= 'a' and <= 'h',
            1 or 3 => c is >= '1' and <= '8',
            4 => c is 'q' or 'r' or 'b' or 'n',
            _ => false,
        };
        if (!keep) return false;

        _renderer.TypedBuffer += c;
        return true;
    }

    private bool TryApplyTyped()
    {
        string buf = _renderer.TypedBuffer;
        if (buf.Length != 4 && buf.Length != 5)
        {
            _renderer.Status = "Type a move like e2e4 (or e7e8q to promote).";
            return true;
        }

        if (_engine.WhoseMove != _engine.HumanPlayer)
        {
            _renderer.Status = "Wait — engine is thinking.";
            return true;
        }

        // Resolve promotion suffix if present.
        if (buf.Length == 5)
        {
            _engine.PromoteToPieceType = buf[4] switch
            {
                'q' => ChessPieceType.Queen,
                'r' => ChessPieceType.Rook,
                'b' => ChessPieceType.Bishop,
                'n' => ChessPieceType.Knight,
                _ => ChessPieceType.Queen,
            };
        }
        else
        {
            _engine.PromoteToPieceType = ChessPieceType.Queen;
        }

        string an = buf.Substring(0, 4);
        _renderer.TypedBuffer = "";

        if (!_engine.MovePieceAN(an))
        {
            _renderer.Status = $"Illegal move: {an}";
            return true;
        }

        AfterHumanMove(an);
        EngineReply();
        return true;
    }

    private bool TryMove(byte sc, byte sr, byte dc, byte dr)
    {
        if (!_engine.MovePiece(sc, sr, dc, dr))
        {
            _renderer.Status = "Illegal move (engine rejected).";
            return false;
        }
        AfterHumanMove($"{Algebraic(sc, sr)}{Algebraic(dc, dr)}");
        return true;
    }

    private void AfterHumanMove(string uci)
    {
        var lm = _engine.LastMove;
        string label = !string.IsNullOrEmpty(lm?.PgnMove) ? lm!.PgnMove : uci;
        _renderer.History.Add(label);
        _renderer.Status = $"You played {label}. Engine to move…";
    }

    private void EngineReply()
    {
        if (_engine.IsGameOver()) return;

        // Re-render once so the user sees their move + "thinking" status.
        _renderer.RenderDynamic();

        _engine.AiPonderMove();

        var lm = _engine.LastMove;
        if (lm != null)
        {
            string label = !string.IsNullOrEmpty(lm.PgnMove)
                ? lm.PgnMove
                : lm.GetPureCoordinateNotation();
            _renderer.History.Add(label);
            _renderer.Status = $"Engine played {label}. Your move.";
        }
    }

    private bool CheckGameOverAndAnnounce()
    {
        if (!_engine.IsGameOver()) return false;

        string outcome;
        if (_engine.GetWhiteMate())
        {
            outcome = _engine.HumanPlayer == ChessPieceColor.White
                ? "[red]Checkmate — you lost.[/]"
                : "[green]Checkmate — you won![/]";
        }
        else if (_engine.GetBlackMate())
        {
            outcome = _engine.HumanPlayer == ChessPieceColor.Black
                ? "[red]Checkmate — you lost.[/]"
                : "[green]Checkmate — you won![/]";
        }
        else if (_engine.StaleMate)
        {
            outcome = "[yellow]Stalemate — draw.[/]";
        }
        else if (_engine.FiftyMove)
        {
            outcome = "[yellow]Draw by 50-move rule.[/]";
        }
        else if (_engine.RepeatedMove)
        {
            outcome = "[yellow]Draw by threefold repetition.[/]";
        }
        else
        {
            outcome = "[yellow]Game over.[/]";
        }

        _renderer.Status = "Game over — press any key to exit.";
        _renderer.RenderDynamic();

        Console.SetCursorPosition(0, BoardRenderer.PromptRow + 1);
        AnsiConsole.Markup(outcome);
        return true;
    }

    private bool IsPromotionMove(byte sc, byte sr, byte dc, byte dr)
    {
        var pt = _engine.GetPieceTypeAt(sc, sr);
        if (pt != ChessPieceType.Pawn) return false;
        // For white: pawn reaches rankTop 0 (= rank 8). For black: rankTop 7 (= rank 1).
        return dr == 0 || dr == 7;
    }

    private ChessPieceType AskPromotion()
    {
        // Inline mini-prompt at the status line. Default queen on anything unexpected.
        Console.SetCursorPosition(0, BoardRenderer.PromptRow - 2);
        Console.Write(new string(' ', 80));
        Console.SetCursorPosition(0, BoardRenderer.PromptRow - 2);
        AnsiConsole.Markup("[bold]Promote to[/] (q=Queen, r=Rook, b=Bishop, n=Knight): ");

        while (true)
        {
            var ev = InputReader.Read();
            if (ev.Kind != InputKind.Char) continue;
            switch (char.ToLowerInvariant(ev.Char))
            {
                case 'q': return ChessPieceType.Queen;
                case 'r': return ChessPieceType.Rook;
                case 'b': return ChessPieceType.Bishop;
                case 'n': return ChessPieceType.Knight;
            }
        }
    }

    private static string Algebraic(byte file, byte rankTop)
    {
        // rankTop 0 = rank 8, rankTop 7 = rank 1.
        return $"{(char)('a' + file)}{8 - rankTop}";
    }
}
