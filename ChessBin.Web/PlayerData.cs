using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

namespace ChessBin.Web;

public sealed record ProgressCodeResult(IReadOnlyDictionary<string, string>? Entries, string? Error)
{
    public bool Success => Entries is not null;
    public static ProgressCodeResult Fail(string error) => new(null, error);
}

/// <summary>
/// Everything a player accumulates lives on their own device. This is the one way to carry it
/// to another one: a code they copy and paste, rather than an account they have to create.
/// <para>
/// The encoding and its validation are deliberately here rather than in JavaScript, because
/// this is where a bad paste has to produce a readable message instead of a lost streak.
/// </para>
/// </summary>
public static class PlayerData
{
    /// <summary>Marks the string as ours, and carries the format version.</summary>
    public const string Prefix = "CBP1-";

    /// <summary>Only our own keys can be written, so a code cannot reach anything else.</summary>
    public const string KeyPrefix = "chessbin.";

    public const int MaxEntries = 32;
    public const int MaxValueLength = 64 * 1024;

    public static string Encode(IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ours = entries
            .Where(e => e.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .Take(MaxEntries)
            .ToDictionary(e => e.Key, e => e.Value);

        string json = JsonSerializer.Serialize(ours);
        return Prefix + ToBase64Url(Encoding.UTF8.GetBytes(json));
    }

    public static ProgressCodeResult Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ProgressCodeResult.Fail("Paste a progress code first.");

        // People paste with line breaks and stray spaces; that should not be a failure.
        string trimmed = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return ProgressCodeResult.Fail(trimmed.StartsWith("CBP", StringComparison.OrdinalIgnoreCase)
                ? "That code comes from a different version of ChessBin."
                : "That doesn't look like a ChessBin progress code.");
        }

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(trimmed[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return ProgressCodeResult.Fail("That code looks damaged — check it copied in full.");
        }

        Dictionary<string, string>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception)
        {
            return ProgressCodeResult.Fail("That code looks damaged — check it copied in full.");
        }

        if (entries is null || entries.Count == 0)
            return ProgressCodeResult.Fail("That code carries no progress.");

        if (entries.Count > MaxEntries)
            return ProgressCodeResult.Fail("That code carries more than ChessBin stores.");

        // A code is untrusted input. It may only touch our own keys, and may not be a way to
        // stuff megabytes into someone's browser.
        foreach ((string key, string value) in entries)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                return ProgressCodeResult.Fail("That code contains something ChessBin doesn't recognise.");
            if (value.Length > MaxValueLength)
                return ProgressCodeResult.Fail("That code contains more data than ChessBin stores.");
        }

        return new ProgressCodeResult(entries, null);
    }

    // ── browser side ────────────────────────────────────────────────────────────

    public static async Task<string> ExportAsync(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        string json = await js.InvokeAsync<string>("chessBin.exportProgress") ?? "{}";

        Dictionary<string, string> entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (Exception)
        {
            entries = [];
        }

        return Encode(entries);
    }

    public static async Task<bool> ImportAsync(IJSRuntime js, IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(entries);
        return await js.InvokeAsync<bool>("chessBin.importProgress", JsonSerializer.Serialize(entries));
    }

    // ── base64url, so a code survives being pasted into anything ────────────────

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}
