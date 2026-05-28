using MoonforgePlay;

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("MoonforgePlay needs an interactive terminal.");
    Console.Error.WriteLine("Run it directly:  dotnet run --project MoonforgePlay -c Release");
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Prevent Ctrl+C from killing the process mid-render — we'll exit cleanly on Esc
// instead (CancelKeyPress handler is the standard way; the raw-VT input layer
// also surfaces Ctrl+C as InputKind.Escape, so users still have an out).
Console.CancelKeyPress += (_, e) => { e.Cancel = true; };

using var modeScope = new ConsoleModeScope();
try
{
    Console.CursorVisible = false;
    new GameApp().Run();
}
finally
{
    Console.CursorVisible = true;
    Console.SetCursorPosition(0, Console.WindowHeight - 1);
    Console.WriteLine();
}
return 0;
