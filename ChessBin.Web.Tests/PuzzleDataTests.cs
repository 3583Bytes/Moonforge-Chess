using ChessBin.Web;
using ChessEngine.Engine;

namespace ChessBin.Web.Tests;

/// <summary>
/// Guards the puzzle data that actually ships. The importer validates puzzles when it
/// generates them, but these run in CI on the committed files, so a corrupted or
/// hand-edited shard can't reach production.
/// </summary>
public sealed class PuzzleDataTests
{
    private static readonly Lazy<(PuzzleManifest Manifest, PuzzleRecord[] Puzzles, string Dir)> Data = new(Load);

    private static string PuzzleDir
    {
        get
        {
            // Walk up from the test assembly to the repo root, then into wwwroot.
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ChessBin.Web", "wwwroot")))
                dir = dir.Parent;

            Assert.That(dir, Is.Not.Null, "could not locate the repo root from the test directory");
            return Path.Combine(dir!.FullName, "ChessBin.Web", "wwwroot", "puzzles");
        }
    }

    private static (PuzzleManifest, PuzzleRecord[], string) Load()
    {
        var dir = PuzzleDir;
        var manifest = PuzzleData.ParseManifest(File.ReadAllText(Path.Combine(dir, "manifest.json")));

        var all = new List<PuzzleRecord>(manifest.Count);
        for (int s = 0; s < manifest.Shards; s++)
            all.AddRange(PuzzleData.ParseShard(File.ReadAllText(Path.Combine(dir, $"shard-{s:D3}.json"))));

        return (manifest, all.ToArray(), dir);
    }

    [Test]
    public void Manifest_MatchesTheShardsOnDisk()
    {
        var (manifest, puzzles, dir) = Data.Value;
        int shardFiles = Directory.GetFiles(dir, "shard-*.json").Length;

        Assert.Multiple(() =>
        {
            Assert.That(manifest.Version, Is.EqualTo(1));
            Assert.That(manifest.License, Is.EqualTo("CC0-1.0"), "the Lichess export is CC0; keep the attribution honest");
            Assert.That(shardFiles, Is.EqualTo(manifest.Shards), "shard count on disk differs from the manifest");
            Assert.That(puzzles, Has.Length.EqualTo(manifest.Count), "puzzle count differs from the manifest");
            Assert.That(manifest.Shards, Is.EqualTo((manifest.Count + manifest.ShardSize - 1) / manifest.ShardSize));
        });
    }

    [Test]
    public void EveryPuzzle_IsWellFormed()
    {
        var (manifest, puzzles, _) = Data.Value;

        Assert.Multiple(() =>
        {
            Assert.That(puzzles.Select(p => p.Id).Distinct().Count(), Is.EqualTo(puzzles.Length), "duplicate puzzle ids");

            foreach (var p in puzzles)
            {
                Assert.That(p.Fen, Is.Not.Empty, $"{p.Id}: empty FEN");
                Assert.That(p.Solution, Is.Not.Empty, $"{p.Id}: no solution moves");
                Assert.That(p.LastMove.Length, Is.InRange(4, 5), $"{p.Id}: malformed setup move");
                Assert.That(p.Rating, Is.InRange(manifest.RatingRange[0], manifest.RatingRange[1]), $"{p.Id}: rating outside the manifest range");
                Assert.That(p.Themes, Is.Not.Empty, $"{p.Id}: no themes");
                foreach (var move in p.Solution)
                    Assert.That(move.Length, Is.InRange(4, 5), $"{p.Id}: malformed solution move '{move}'");
            }
        });
    }

    [Test]
    public void EveryPuzzle_ReplaysLegallyThroughTheEngine()
    {
        var (_, puzzles, _) = Data.Value;

        // One Engine for the whole sweep — its constructor loads the opening book.
        var engine = new Engine();
        var failures = new List<string>();
        int matesChecked = 0;

        foreach (var p in puzzles)
        {
            try
            {
                engine.SetPosition(p.Fen);

                for (int i = 0; i < p.Solution.Length; i++)
                {
                    if (!PuzzleData.TryApplyUci(engine, p.Solution[i]))
                    {
                        failures.Add($"{p.Id}: move {i} '{p.Solution[i]}' is not legal in {p.Fen}");
                        break;
                    }
                }

                // Lichess labels these as mates; Moonforge must agree, or one of the two is wrong.
                if (p.Themes.Any(t => t.StartsWith("mateIn", StringComparison.Ordinal) || t == "mate"))
                {
                    matesChecked++;
                    if (!engine.IsGameOver())
                        failures.Add($"{p.Id}: themed as mate but the line does not end the game");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{p.Id}: threw {ex.GetType().Name} — {ex.Message}");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(10)));
            Assert.That(matesChecked, Is.GreaterThan(0), "expected at least some mate puzzles to cross-check");
        });
    }

    /// <summary>
    /// The puzzle page auto-promotes to whatever the solution says, which is only safe while
    /// every promotion is to a queen. An underpromotion puzzle would need a picker instead,
    /// because choosing the piece would BE the puzzle — so fail loudly if one ever lands.
    /// </summary>
    [Test]
    public void NoPuzzle_RequiresAnUnderpromotion()
    {
        var (_, puzzles, _) = Data.Value;

        var offenders = puzzles
            .Where(p => p.Solution.Where((move, i) => i % 2 == 0 && move.Length == 5 && move[4] != 'q').Any())
            .Select(p => p.Id)
            .ToArray();

        Assert.That(offenders, Is.Empty,
            "these puzzles need an underpromotion, so the auto-promote shortcut in PuzzleSession no longer holds: "
            + string.Join(", ", offenders.Take(10)));
    }

    [Test]
    public void DailyRotation_IsStableAndCoversEveryPuzzle()
    {
        var (manifest, _, _) = Data.Value;
        var date = new DateOnly(2026, 8, 13);

        // Computed outside the code under test, so this pins the rotation rule itself
        // rather than restating it. Stays valid if the puzzle count ever changes.
        const int DaysFromEpochToDate = 20678;

        Assert.Multiple(() =>
        {
            Assert.That(PuzzleData.IndexForDate(date, manifest.Count),
                Is.EqualTo(DaysFromEpochToDate % manifest.Count), "the daily rotation rule changed");

            Assert.That(PuzzleData.IndexForDate(date.AddDays(1), manifest.Count),
                Is.Not.EqualTo(PuzzleData.IndexForDate(date, manifest.Count)), "consecutive days must differ");

            // A full cycle should touch every puzzle exactly once.
            var seen = new HashSet<int>();
            for (int d = 0; d < manifest.Count; d++)
                seen.Add(PuzzleData.IndexForDate(date.AddDays(d), manifest.Count));
            Assert.That(seen, Has.Count.EqualTo(manifest.Count), "the rotation skips or repeats puzzles within one cycle");

            // Dates before the epoch must not produce a negative index.
            Assert.That(PuzzleData.IndexForDate(new DateOnly(1969, 6, 1), manifest.Count), Is.InRange(0, manifest.Count - 1));
        });
    }

    [Test]
    public void ShardAddressing_RoundTripsToTheRightPuzzle()
    {
        var (manifest, puzzles, dir) = Data.Value;

        foreach (int index in new[] { 0, 1, manifest.ShardSize - 1, manifest.ShardSize, manifest.Count - 1 })
        {
            int shard = PuzzleData.ShardIndexFor(index, manifest.ShardSize);
            int offset = PuzzleData.OffsetWithinShard(index, manifest.ShardSize);

            var loaded = PuzzleData.ParseShard(File.ReadAllText(Path.Combine(dir, Path.GetFileName(PuzzleData.ShardPath(shard)))));

            Assert.That(loaded[offset].Id, Is.EqualTo(puzzles[index].Id),
                $"index {index} should resolve to shard {shard} offset {offset}");
        }
    }
}
