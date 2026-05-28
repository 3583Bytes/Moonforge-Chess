using ChessEngine.Engine;
using Spectre.Console;

namespace MoonforgePlay;

/// <summary>
/// Renders the chess board and side panel to fixed terminal coordinates.
/// The fixed origin matters because mouse clicks come back as raw terminal
/// (col,row) and we map them straight to board (file, rankFromTop) without
/// having to query Spectre's layout system.
/// </summary>
internal sealed class BoardRenderer
{
    // Layout constants. Origin = top-left of the playable board area
    // (NOT including the rank-label column). Each square is 3 chars wide x 1 row tall.
    public const int BoardOriginCol = 4;
    public const int BoardOriginRow = 4;
    public const int SquareWidth = 3;
    public const int SquareHeight = 1;
    public const int BoardCols = SquareWidth * 8;
    public const int BoardRows = SquareHeight * 8;

    // Side panel layout.
    private const int PanelOriginCol = BoardOriginCol + BoardCols + 4;
    private const int PanelOriginRow = BoardOriginRow;

    // Input prompt sits below the board.
    public const int PromptRow = BoardOriginRow + BoardRows + 3;

    private readonly Engine _engine;
    public BoardRenderer(Engine engine) { _engine = engine; }

    /// <summary>Selected square for the click-to-move flow, or null.</summary>
    public (byte col, byte row)? Selected { get; set; }

    /// <summary>Legal destinations for the currently selected piece (empty if no selection).</summary>
    public HashSet<(byte col, byte row)> LegalDests { get; } = new();

    /// <summary>Move-history strings, newest last, displayed in the side panel.</summary>
    public List<string> History { get; } = new();

    /// <summary>Transient status line shown above the prompt (errors, last move feedback).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Currently-typed move-in-progress (echoed below the board).</summary>
    public string TypedBuffer { get; set; } = string.Empty;

    /// <summary>
    /// Map a terminal (col, row) to a board (file, rankFromTop) if it lands on the board.
    /// rankFromTop 0 = rank 8 (Black's back rank); rankFromTop 7 = rank 1 (White's back rank).
    /// </summary>
    public bool TryHitTest(int col, int row, out byte file, out byte rankFromTop)
    {
        file = 0; rankFromTop = 0;
        if (col < BoardOriginCol || col >= BoardOriginCol + BoardCols) return false;
        if (row < BoardOriginRow || row >= BoardOriginRow + BoardRows) return false;
        file = (byte)((col - BoardOriginCol) / SquareWidth);
        rankFromTop = (byte)((row - BoardOriginRow) / SquareHeight);
        return true;
    }

    public void RenderAll()
    {
        AnsiConsole.Clear();
        DrawTitle();
        DrawFileLabels();
        DrawRankLabels();
        DrawBoard();
        DrawSidePanel();
        DrawStatus();
        DrawPrompt();
    }

    /// <summary>Re-render just the dynamic regions (board, status, prompt). Cheaper than full clear.</summary>
    public void RenderDynamic()
    {
        DrawBoard();
        DrawSidePanel();
        DrawStatus();
        DrawPrompt();
    }

    private static void At(int col, int row) => Console.SetCursorPosition(col, row);

    private void DrawTitle()
    {
        At(0, 0);
        AnsiConsole.Markup("[bold deepskyblue1]Moonforge Chess[/] [grey]— click a piece, click a destination, or type e.g. e2e4. Esc to quit.[/]");
    }

    private void DrawFileLabels()
    {
        At(BoardOriginCol, BoardOriginRow - 1);
        for (int f = 0; f < 8; f++)
        {
            AnsiConsole.Markup($"[grey] {(char)('a' + f)} [/]");
        }
    }

    private void DrawRankLabels()
    {
        for (int r = 0; r < 8; r++)
        {
            At(BoardOriginCol - 2, BoardOriginRow + r);
            // Top row = rank 8.
            AnsiConsole.Markup($"[grey]{8 - r}[/]");
        }
    }

    private void DrawBoard()
    {
        for (byte rankTop = 0; rankTop < 8; rankTop++)
        {
            for (byte file = 0; file < 8; file++)
            {
                DrawSquare(file, rankTop);
            }
        }
    }

    private void DrawSquare(byte file, byte rankTop)
    {
        At(BoardOriginCol + file * SquareWidth, BoardOriginRow + rankTop);

        // Square colors. Light = warm cream, dark = warm brown.
        bool isLight = ((file + rankTop) % 2) == 0;
        string bg = isLight ? "wheat1" : "darkorange3_1";

        // Selection / legal-dest overlays.
        bool isSelected = Selected is { } sel && sel.col == file && sel.row == rankTop;
        bool isLegalDest = LegalDests.Contains((file, rankTop));
        if (isSelected) bg = "yellow";
        else if (isLegalDest) bg = isLight ? "palegreen3_1" : "darkgreen";

        var pt = _engine.GetPieceTypeAt(file, rankTop);
        var pc = _engine.GetPieceColorAt(file, rankTop);

        char glyph = pt switch
        {
            ChessPieceType.King => 'K',
            ChessPieceType.Queen => 'Q',
            ChessPieceType.Rook => 'R',
            ChessPieceType.Bishop => 'B',
            ChessPieceType.Knight => 'N',
            ChessPieceType.Pawn => 'P',
            _ => ' ',
        };

        string fg;
        if (pt == ChessPieceType.None)
        {
            // Show a centred dot on legal-destination empty squares for visibility.
            string mid = isLegalDest ? "·" : " ";
            AnsiConsole.Markup($"[grey on {bg}] {mid} [/]");
            return;
        }
        fg = pc == ChessPieceColor.White ? "white" : "grey7";
        // For black pieces on light squares we want enough contrast; grey7 is near-black.
        AnsiConsole.Markup($"[bold {fg} on {bg}] {glyph} [/]");
    }

    private void DrawSidePanel()
    {
        // Whose move + check flag.
        At(PanelOriginCol, PanelOriginRow);
        ClearLine(40);
        At(PanelOriginCol, PanelOriginRow);
        string side = _engine.WhoseMove == ChessPieceColor.White ? "[white]White[/]" : "[grey]Black[/]";
        string flag = _engine.GetWhiteCheck() || _engine.GetBlackCheck() ? " [red](in check)[/]" : "";
        AnsiConsole.Markup($"To move: {side}{flag}");

        // Eval (last completed search score, side-to-move POV reported by engine).
        At(PanelOriginCol, PanelOriginRow + 1);
        ClearLine(40);
        At(PanelOriginCol, PanelOriginRow + 1);
        int cp = _engine.SearchScore;
        string evalStr = Math.Abs(cp) > 9000
            ? (cp > 0 ? "+M" : "-M")
            : $"{cp / 100.0:+0.00;-0.00;0.00}";
        AnsiConsole.Markup($"Eval: [yellow]{evalStr}[/]   Depth: {_engine.PlyDepthReached}");

        // Move history list (last 12 moves, two columns per full move).
        At(PanelOriginCol, PanelOriginRow + 3);
        AnsiConsole.Markup("[underline grey]Moves[/]");
        const int maxRows = 12;
        for (int i = 0; i < maxRows; i++)
        {
            At(PanelOriginCol, PanelOriginRow + 4 + i);
            ClearLine(40);
            int historyIdx = History.Count - (maxRows - i) * 2;
            if (historyIdx >= 0 && historyIdx < History.Count)
            {
                int moveNum = historyIdx / 2 + 1;
                string white = historyIdx < History.Count ? History[historyIdx] : "";
                string black = (historyIdx + 1) < History.Count ? History[historyIdx + 1] : "";
                At(PanelOriginCol, PanelOriginRow + 4 + i);
                AnsiConsole.Markup($"[grey]{moveNum,3}.[/] {white,-7} {black,-7}");
            }
        }
    }

    private void DrawStatus()
    {
        At(0, PromptRow - 2);
        ClearLine(120);
        At(0, PromptRow - 2);
        if (!string.IsNullOrEmpty(Status))
        {
            AnsiConsole.Markup($"[grey]{Markup.Escape(Status)}[/]");
        }
    }

    private void DrawPrompt()
    {
        At(0, PromptRow);
        ClearLine(120);
        At(0, PromptRow);
        AnsiConsole.Markup($"[bold]Your move[/] » [yellow]{Markup.Escape(TypedBuffer)}[/][grey]_[/]");
    }

    private static void ClearLine(int width)
    {
        Console.Write(new string(' ', width));
    }
}
