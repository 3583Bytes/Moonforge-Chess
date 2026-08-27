using ChessBin.Web;

namespace ChessBin.Web.Tests;

/// <summary>
/// Which sound a move makes is read off its notation, because the engine already encodes
/// everything needed there: it appends "+" or "#" when applying the move, and algebraic
/// notation carries "x" for a capture and "O-O" for castling.
/// </summary>
public sealed class MoveSoundTests
{
    private static string Kind(string label) => new PlayedMove("a1a2", label, 0, 0).SoundKind;

    [Test]
    public void EachKindOfMoveGetsItsOwnSound()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Kind("e4"), Is.EqualTo("move"));
            Assert.That(Kind("Nf3"), Is.EqualTo("move"));
            Assert.That(Kind("exd5"), Is.EqualTo("capture"));
            Assert.That(Kind("Nxe5"), Is.EqualTo("capture"));
            Assert.That(Kind("Bb5+"), Is.EqualTo("check"));
            Assert.That(Kind("Qh5#"), Is.EqualTo("mate"));
            Assert.That(Kind("O-O"), Is.EqualTo("castle"));
            Assert.That(Kind("O-O-O"), Is.EqualTo("castle"));
        });
    }

    [Test]
    public void TheMoreFinalOutcomeWinsWhenAMoveIsSeveralThingsAtOnce()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Kind("Nxe5+"), Is.EqualTo("check"), "a capture that checks should sound like a check");
            Assert.That(Kind("Qxf7#"), Is.EqualTo("mate"), "and mate outranks everything");
            Assert.That(Kind("O-O+"), Is.EqualTo("check"));
            Assert.That(Kind("exd8=Q#"), Is.EqualTo("mate"));
        });
    }

    [Test]
    public void AnUnknownOrEmptyLabelStillMakesASound()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Kind(""), Is.EqualTo("move"), "silence would read as a bug");
            Assert.That(Kind("e2e4"), Is.EqualTo("move"), "a raw coordinate move is still a move");
        });
    }

    [Test]
    public void TheSharedAudioContextIsCreatedOnceRatherThanPerMove()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
            dir = dir.Parent;

        string js = File.ReadAllText(Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot", "js", "chessbin.js"));

        Assert.Multiple(() =>
        {
            // The old version built a context per move, which browsers cap — sound stopped
            // working after a handful of moves.
            Assert.That(js.Split("new Context()").Length - 1, Is.EqualTo(1),
                "there should be exactly one place that constructs an AudioContext");
            Assert.That(js, Does.Contain("__chessBinCtx"), "and it should be cached");
            Assert.That(js, Does.Contain("resume()"), "contexts start suspended until a gesture");
            foreach (string kind in new[] { "capture", "check", "mate", "castle" })
                Assert.That(js, Does.Contain($"\"{kind}\""), $"no sound is defined for {kind}");
        });
    }
}
