namespace MoonforgePlay;

internal enum InputKind
{
    Char,
    Backspace,
    Enter,
    Escape,
    MouseClick,
}

internal readonly record struct InputEvent(
    InputKind Kind,
    char Char = '\0',
    int MouseCol = -1,
    int MouseRow = -1);

/// <summary>
/// Reads stdin one char at a time, decoding ANSI mouse and key escapes into
/// <see cref="InputEvent"/>s. Sized for our use case (chess input prompt):
/// only mouse press, plain typed chars, backspace, enter, and escape are
/// surfaced — arrow keys and other CSI sequences are silently dropped.
/// </summary>
internal static class InputReader
{
    public static InputEvent Read()
    {
        while (true)
        {
            int ch = Console.Read();
            if (ch < 0)
            {
                // stdin closed — treat as Escape to break the caller's loop.
                return new InputEvent(InputKind.Escape);
            }

            if (ch == 0x1b) // ESC
            {
                // Could be a bare ESC or the start of a CSI/SS3 sequence.
                // Peek the next char with a short polling window; if nothing
                // arrives within ~30ms, treat as a real ESC keypress.
                if (!WaitForByte(30))
                {
                    return new InputEvent(InputKind.Escape);
                }
                int next = Console.Read();
                if (next != '[')
                {
                    // Not a CSI we understand — swallow and keep reading.
                    continue;
                }

                // CSI sequence. Mouse SGR format is: CSI < params M|m
                // Key sequences (arrows, F-keys, …) come as CSI params ~ or letter.
                int third = Console.Read();
                if (third == '<')
                {
                    if (TryReadMouseSgr(out int btn, out int col, out int row, out bool press))
                    {
                        // Only surface presses; ignore releases and motion.
                        if (press && btn == 0)
                        {
                            // 1-based VT coords -> 0-based screen coords.
                            return new InputEvent(InputKind.MouseClick, MouseCol: col - 1, MouseRow: row - 1);
                        }
                    }
                    continue;
                }

                // Some other CSI we don't care about — drain until a final byte.
                DrainCsi(third);
                continue;
            }

            switch (ch)
            {
                case '\r':
                case '\n':
                    return new InputEvent(InputKind.Enter);
                case 0x7f:
                case 0x08:
                    return new InputEvent(InputKind.Backspace);
                case 0x03: // Ctrl+C
                    return new InputEvent(InputKind.Escape);
            }

            if (ch >= 0x20 && ch < 0x7f)
            {
                return new InputEvent(InputKind.Char, Char: (char)ch);
            }
            // Unknown control byte; keep reading.
        }
    }

    private static bool WaitForByte(int timeoutMs)
    {
        // Console.Read blocks; we want non-blocking. On Windows in raw-VT mode
        // there is no portable async stdin, so we poll Console.KeyAvailable
        // (it inspects the input queue without consuming) at ~5ms intervals.
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (Console.KeyAvailable) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    private static bool TryReadMouseSgr(out int button, out int col, out int row, out bool press)
    {
        // Format already consumed: ESC [ <
        // Remaining: button ; col ; row ; (M|m)
        button = col = row = 0;
        press = false;

        if (!ReadInt(out button, out int sep) || sep != ';') return false;
        if (!ReadInt(out col, out sep) || sep != ';') return false;
        if (!ReadInt(out row, out sep)) return false;
        press = sep == 'M';
        return sep == 'M' || sep == 'm';
    }

    private static bool ReadInt(out int value, out int terminator)
    {
        value = 0;
        terminator = -1;
        bool any = false;
        while (true)
        {
            int c = Console.Read();
            if (c < 0) return false;
            if (c >= '0' && c <= '9')
            {
                value = value * 10 + (c - '0');
                any = true;
                continue;
            }
            terminator = c;
            return any;
        }
    }

    private static void DrainCsi(int firstSeen)
    {
        // Final byte of a CSI is in 0x40-0x7E. Drain until we hit one.
        int c = firstSeen;
        while (c >= 0 && !(c >= 0x40 && c <= 0x7e))
        {
            c = Console.Read();
        }
    }
}
