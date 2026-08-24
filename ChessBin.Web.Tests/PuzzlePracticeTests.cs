using ChessBin.Web;

namespace ChessBin.Web.Tests;

/// <summary>
/// Practice mode draws from a single shard, which only works because the importer spreads
/// every rating band across every shard. These tests hold that property and the picker's
/// fallback behaviour.
/// </summary>
public sealed class PuzzlePracticeTests
{
    private static string PuzzleDir
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot", "puzzles");
        }
    }

    private static PuzzleRecord[] Shard(int index) =>
        PuzzleData.ParseShard(File.ReadAllText(Path.Combine(PuzzleDir, $"shard-{index:D3}.json")));

    private static PuzzleManifest Manifest() =>
        PuzzleData.ParseManifest(File.ReadAllText(Path.Combine(PuzzleDir, "manifest.json")));

    [Test]
    public void EveryFullShard_CoversEveryDifficultyBand()
    {
        var manifest = Manifest();

        // The last shard is a partial remainder, so it isn't required to hold the full spread.
        int fullShards = manifest.Count / manifest.ShardSize;

        Assert.Multiple(() =>
        {
            for (int s = 0; s < fullShards; s++)
            {
                var shard = Shard(s);
                foreach (RatingBand band in new[] { RatingBand.Easy, RatingBand.Medium, RatingBand.Hard })
                {
                    (int min, int max) = PuzzleData.BandRange(band);
                    Assert.That(shard.Any(p => p.Rating >= min && p.Rating <= max), Is.True,
                        $"shard {s} has nothing in the {band} band, so one fetch can't serve that difficulty");
                }
            }
        });
    }

    [Test]
    public void PickPractice_RespectsTheRequestedBand()
    {
        var shard = Shard(0);
        var rng = new Random(1234);

        Assert.Multiple(() =>
        {
            foreach (RatingBand band in new[] { RatingBand.Easy, RatingBand.Medium, RatingBand.Hard })
            {
                (int min, int max) = PuzzleData.BandRange(band);
                for (int i = 0; i < 25; i++)
                {
                    var picked = PuzzleData.PickPractice(shard, band, new HashSet<string>(), rng);
                    Assert.That(picked, Is.Not.Null);
                    Assert.That(picked!.Rating, Is.InRange(min, Math.Min(max, 4000)), $"{band} returned a {picked.Rating} puzzle");
                }
            }
        });
    }

    [Test]
    public void PickPractice_SkipsPuzzlesAlreadySeen()
    {
        var shard = Shard(0);
        var rng = new Random(7);
        var seen = new HashSet<string>();

        // Draw the whole Easy band; every draw should be new until it's exhausted.
        (int min, int max) = PuzzleData.BandRange(RatingBand.Easy);
        int inBand = shard.Count(p => p.Rating >= min && p.Rating <= max);

        for (int i = 0; i < inBand; i++)
        {
            var picked = PuzzleData.PickPractice(shard, RatingBand.Easy, seen, rng);
            Assert.That(picked, Is.Not.Null);
            Assert.That(seen.Add(picked!.Id), Is.True, "practice served a repeat while unseen puzzles remained");
        }

        Assert.That(seen, Has.Count.EqualTo(inBand));
    }

    [Test]
    public void PickPractice_KeepsGoingOnceEverythingHasBeenSeen()
    {
        var shard = Shard(0);
        var seen = new HashSet<string>(shard.Select(p => p.Id));      // seen the entire shard

        var picked = PuzzleData.PickPractice(shard, RatingBand.Any, seen, new Random(3));

        Assert.That(picked, Is.Not.Null, "a player who has seen everything should get a repeat, not a dead end");
    }

    [Test]
    public void PickPractice_FallsBackWhenTheBandIsEmpty()
    {
        // A shard-sized pool with nothing in the Hard band.
        PuzzleRecord[] easyOnly =
        [
            new("a", "8/8/8/8/8/8/8/K6k w - - 0 1", "a1a2", ["a1a2"], 1010, ["x"], ""),
            new("b", "8/8/8/8/8/8/8/K6k w - - 0 1", "a1a2", ["a1a2"], 1020, ["x"], ""),
        ];

        var picked = PuzzleData.PickPractice(easyOnly, RatingBand.Hard, new HashSet<string>(), new Random(5));

        Assert.That(picked, Is.Not.Null, "an empty band should widen rather than return nothing");
    }

    [Test]
    public void BandRanges_TileTheWholeSetWithoutGaps()
    {
        var manifest = Manifest();
        var all = Enumerable.Range(0, manifest.Shards).SelectMany(Shard).ToArray();

        (int easyMin, int easyMax) = PuzzleData.BandRange(RatingBand.Easy);
        (int medMin, int medMax) = PuzzleData.BandRange(RatingBand.Medium);
        (int hardMin, _) = PuzzleData.BandRange(RatingBand.Hard);

        Assert.Multiple(() =>
        {
            Assert.That(medMin, Is.EqualTo(easyMax + 1), "a rating between Easy and Medium would be unreachable");
            Assert.That(hardMin, Is.EqualTo(medMax + 1), "a rating between Medium and Hard would be unreachable");
            Assert.That(all.Count(p => p.Rating >= easyMin && p.Rating <= easyMax), Is.GreaterThan(0));
            Assert.That(all.Count(p => p.Rating >= medMin && p.Rating <= medMax), Is.GreaterThan(0));
            Assert.That(all.Count(p => p.Rating >= hardMin), Is.GreaterThan(0));
        });
    }
}
