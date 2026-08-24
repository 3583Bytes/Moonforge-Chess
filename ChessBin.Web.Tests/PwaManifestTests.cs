using System.Buffers.Binary;
using System.Text.Json;

namespace ChessBin.Web.Tests;

/// <summary>
/// Installability is decided by a browser we cannot run here, but every input it checks is a
/// file on disk. These assert the parts that fail silently: a manifest missing a field a
/// browser requires, or an icon whose declared size is not its real size.
/// </summary>
public sealed class PwaManifestTests
{
    private static string Wwwroot
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot");
        }
    }

    private static JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Wwwroot, "site.webmanifest"))).RootElement;

    /// <summary>Reads width and height straight out of the PNG IHDR chunk.</summary>
    private static (int Width, int Height) PngSize(string path)
    {
        byte[] header = new byte[24];
        using FileStream stream = File.OpenRead(path);
        Assert.That(stream.Read(header), Is.EqualTo(24), $"{path} is too short to be a PNG");
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }

    [Test]
    public void TheManifestCarriesEverythingABrowserRequiresToOfferInstallation()
    {
        JsonElement m = Manifest();

        Assert.Multiple(() =>
        {
            foreach (string required in new[] { "name", "short_name", "start_url", "display", "icons" })
                Assert.That(m.TryGetProperty(required, out _), Is.True, $"manifest is missing \"{required}\"");

            Assert.That(m.GetProperty("display").GetString(), Is.AnyOf("standalone", "fullscreen", "minimal-ui"),
                "browsers only offer installation for an app-like display mode");
            Assert.That(m.GetProperty("start_url").GetString(), Is.Not.Empty);
            Assert.That(m.GetProperty("short_name").GetString()!.Length, Is.LessThanOrEqualTo(12),
                "short_name is what fits under a home-screen icon");
        });
    }

    [Test]
    public void EveryDeclaredIconExistsAndIsTheSizeItClaims()
    {
        JsonElement m = Manifest();
        var declared = new List<(string Src, int Size)>();

        Assert.Multiple(() =>
        {
            foreach (JsonElement icon in m.GetProperty("icons").EnumerateArray())
            {
                string src = icon.GetProperty("src").GetString()!;
                string sizes = icon.GetProperty("sizes").GetString()!;
                string path = Path.Combine(Wwwroot, src.Replace('/', Path.DirectorySeparatorChar));

                Assert.That(File.Exists(path), Is.True, $"manifest points at {src}, which is not there");

                int expected = int.Parse(sizes.Split('x')[0]);
                (int width, int height) = PngSize(path);
                Assert.That(width, Is.EqualTo(expected), $"{src} declares {sizes} but is {width}x{height}");
                Assert.That(height, Is.EqualTo(expected), $"{src} declares {sizes} but is {width}x{height}");

                declared.Add((src, expected));
            }

            // Chrome wants both a small and a large icon before it will offer installation.
            Assert.That(declared.Any(d => d.Size >= 192), Is.True, "an icon of at least 192px is required");
            Assert.That(declared.Any(d => d.Size >= 512), Is.True, "an icon of at least 512px is required");
        });
    }

    [Test]
    public void EveryHomeScreenShortcutPointsAtARouteThatActuallyExists()
    {
        JsonElement m = Manifest();
        Assert.That(m.TryGetProperty("shortcuts", out JsonElement shortcuts), Is.True);

        Assert.Multiple(() =>
        {
            foreach (JsonElement shortcut in shortcuts.EnumerateArray())
            {
                string url = shortcut.GetProperty("url").GetString()!;
                string boot = Path.Combine(Wwwroot, url.Trim('/').Replace('/', Path.DirectorySeparatorChar), "index.html");
                Assert.That(File.Exists(boot), Is.True, $"shortcut \"{url}\" has no boot page at {boot}");
                Assert.That(shortcut.GetProperty("short_name").GetString()!.Length, Is.LessThanOrEqualTo(12));
            }
        });
    }

    [Test]
    public void TheServiceWorkerPairExistsAndTheDevelopmentOneStaysInert()
    {
        string dev = Path.Combine(Wwwroot, "service-worker.js");
        string published = Path.Combine(Wwwroot, "service-worker.published.js");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(dev), Is.True, "the development worker is what gets registered locally");
            Assert.That(File.Exists(published), Is.True, "publish swaps this one in");

            string devSource = File.ReadAllText(dev);
            Assert.That(devSource, Does.Not.Contain("assetsManifest"),
                "the development worker must not cache, or local edits stop appearing");

            string liveSource = File.ReadAllText(published);
            Assert.That(liveSource, Does.Contain("assetsManifest"), "the published worker precaches from the manifest");
            Assert.That(liveSource, Does.Contain("addEventListener('fetch'"),
                "a fetch handler is what makes the app installable at all");
        });
    }

    [Test]
    public void TheWorkerIsRegisteredFromTheSharedScriptEveryBootPageLoads()
    {
        string shared = File.ReadAllText(Path.Combine(Wwwroot, "js", "chessbin.js"));

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain("serviceWorker.register"), "nothing would ever install the worker");
            Assert.That(shared, Does.Contain("/service-worker.js"),
                "registering a relative path from /puzzle/ would scope the worker to that folder");
        });
    }
}
