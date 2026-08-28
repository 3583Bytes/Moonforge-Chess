using ChessBin.Web;

namespace ChessBin.Web.Tests;

/// <summary>
/// A progress code is untrusted text someone pastes. These cover the round trip and every way
/// a paste can go wrong, because the failure mode to avoid is a readable streak becoming an
/// unreadable error — or worse, a code writing somewhere it shouldn't.
/// </summary>
public sealed class PlayerDataTests
{
    private static Dictionary<string, string> Sample => new()
    {
        ["chessbin.puzzle"] = """{"lastSolved":"2026-08-27","streak":9,"best":14,"totalSolved":63}""",
        ["chessbin.settings"] = """{"side":"White","difficulty":"Medium"}""",
    };

    [Test]
    public void ACodeRoundTripsExactly()
    {
        string code = PlayerData.Encode(Sample);
        ProgressCodeResult back = PlayerData.Decode(code);

        Assert.Multiple(() =>
        {
            Assert.That(code, Does.StartWith(PlayerData.Prefix));
            Assert.That(back.Success, Is.True, back.Error);
            Assert.That(back.Entries, Is.EquivalentTo(Sample));
        });
    }

    [Test]
    public void ACodeSurvivesBeingPastedBadly()
    {
        string code = PlayerData.Encode(Sample);
        string mangled = "  " + code[..20] + "\n" + code[20..] + "  \r\n";

        ProgressCodeResult back = PlayerData.Decode(mangled);

        Assert.Multiple(() =>
        {
            Assert.That(back.Success, Is.True, back.Error);
            Assert.That(back.Entries, Is.EquivalentTo(Sample));
            // No characters that a mail client or chat app will break across lines.
            Assert.That(code, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
        });
    }

    [Test]
    public void EveryWayAPasteCanFailSaysSomethingUseful()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlayerData.Decode(null).Error, Does.Contain("Paste"));
            Assert.That(PlayerData.Decode("   ").Error, Does.Contain("Paste"));
            Assert.That(PlayerData.Decode("hello there").Error, Does.Contain("doesn't look like"));
            Assert.That(PlayerData.Decode("CBP9-abc").Error, Does.Contain("different version"));
            Assert.That(PlayerData.Decode(PlayerData.Prefix + "!!!not base64!!!").Error, Does.Contain("damaged"));
            Assert.That(PlayerData.Decode(PlayerData.Prefix + "bm90anNvbg").Error, Does.Contain("damaged"));

            foreach (string bad in new[] { "hello there", "CBP9-abc", PlayerData.Prefix + "!!!" })
                Assert.That(PlayerData.Decode(bad).Entries, Is.Null, "a failed decode must not return data");
        });
    }

    [Test]
    public void ACodeCannotWriteOutsideChessBinsOwnKeys()
    {
        // Hand-built code carrying a key that is not ours.
        string hostile = PlayerData.Prefix + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("""{"evil.key":"x"}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        ProgressCodeResult result = PlayerData.Decode(hostile);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "a code must not be a way to write arbitrary storage keys");
            Assert.That(result.Error, Does.Contain("doesn't recognise"));
        });
    }

    [Test]
    public void EncodingIgnoresAnythingThatIsNotOurs()
    {
        var mixed = new Dictionary<string, string>(Sample) { ["someone.else"] = "value" };

        ProgressCodeResult back = PlayerData.Decode(PlayerData.Encode(mixed));

        Assert.Multiple(() =>
        {
            Assert.That(back.Success, Is.True, back.Error);
            Assert.That(back.Entries!.Keys, Is.EquivalentTo(Sample.Keys));
        });
    }

    [Test]
    public void AnAbsurdlyLargeValueIsRefused()
    {
        var huge = new Dictionary<string, string>
        {
            ["chessbin.puzzle"] = new string('x', PlayerData.MaxValueLength + 1),
        };

        ProgressCodeResult back = PlayerData.Decode(PlayerData.Encode(huge));

        Assert.Multiple(() =>
        {
            Assert.That(back.Success, Is.False, "a code should not be able to fill someone's storage");
            Assert.That(back.Error, Does.Contain("more data"));
        });
    }

    [Test]
    public void AnEmptyStoreProducesACodeThatSaysSoRatherThanAppearingToWork()
    {
        ProgressCodeResult back = PlayerData.Decode(PlayerData.Encode(new Dictionary<string, string>()));

        Assert.Multiple(() =>
        {
            Assert.That(back.Success, Is.False);
            Assert.That(back.Error, Does.Contain("no progress"));
        });
    }

    [Test]
    public void ARealisticCodeIsShortEnoughToPasteIntoAMessage()
    {
        string code = PlayerData.Encode(Sample);
        Assert.That(code.Length, Is.LessThan(400), $"a {code.Length}-character code is awkward to move by hand");
    }
}
