using System.Runtime.InteropServices;

namespace MoonforgePlay;

/// <summary>
/// Sets the Windows console into VT mode for raw keyboard + mouse input,
/// then restores the previous mode on dispose. Reading stdin one char at a
/// time only works after ENABLE_LINE_INPUT/ENABLE_ECHO_INPUT are off, and
/// mouse events only come through stdin once ENABLE_VIRTUAL_TERMINAL_INPUT
/// is on and the ANSI mouse-tracking escape has been emitted.
/// </summary>
internal sealed class ConsoleModeScope : IDisposable
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;

    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    private const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly IntPtr _stdin;
    private readonly IntPtr _stdout;
    private readonly uint _origIn;
    private readonly uint _origOut;
    private readonly bool _supported;

    public bool MouseSupported => _supported;

    public ConsoleModeScope()
    {
        if (!OperatingSystem.IsWindows())
        {
            _supported = false;
            return;
        }

        _stdin = GetStdHandle(STD_INPUT_HANDLE);
        _stdout = GetStdHandle(STD_OUTPUT_HANDLE);

        if (!GetConsoleMode(_stdin, out _origIn) || !GetConsoleMode(_stdout, out _origOut))
        {
            _supported = false;
            return;
        }

        // Input: turn off line buffering and echo, turn on VT + mouse + extended flags.
        // ENABLE_EXTENDED_FLAGS is required to take effect on ENABLE_MOUSE_INPUT.
        // Drop ENABLE_PROCESSED_INPUT so Ctrl+C reaches us as a char (we still install
        // a CancelKeyPress handler to exit gracefully).
        uint newIn = (_origIn
                      & ~ENABLE_LINE_INPUT
                      & ~ENABLE_ECHO_INPUT
                      & ~ENABLE_PROCESSED_INPUT)
                     | ENABLE_EXTENDED_FLAGS
                     | ENABLE_MOUSE_INPUT
                     | ENABLE_VIRTUAL_TERMINAL_INPUT;
        if (!SetConsoleMode(_stdin, newIn))
        {
            _supported = false;
            return;
        }

        uint newOut = _origOut | ENABLE_VIRTUAL_TERMINAL_PROCESSING | ENABLE_PROCESSED_OUTPUT;
        SetConsoleMode(_stdout, newOut);

        // Enable SGR-style mouse reporting:
        //   ?1000h  — button press/release events
        //   ?1006h  — SGR extended format (no 224-col limit; reports as CSI <btn;col;row;M|m)
        Console.Write("\x1b[?1000h\x1b[?1006h");

        _supported = true;
    }

    public void Dispose()
    {
        if (!_supported) return;

        // Disable mouse reporting before restoring modes — otherwise the terminal
        // will keep emitting mouse sequences into whatever runs after us.
        Console.Write("\x1b[?1000l\x1b[?1006l");

        SetConsoleMode(_stdin, _origIn);
        SetConsoleMode(_stdout, _origOut);
    }
}
