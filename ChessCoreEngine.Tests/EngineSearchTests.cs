using System.Collections.Generic;
using System.Threading;
using ChessEngine.Engine;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

[TestFixture]
[NonParallelizable]
public class EngineSearchTests
{
    private const string Kiwipete =
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    [Test]
    public void SearchBestMove_DoesNotMutateTheCurrentPosition()
    {
        var engine = new Engine(Kiwipete) { PlyDepthSearched = 3 };
        string before = engine.FEN;
        int historyCount = engine.GetMoveHistory().Count;

        EngineSearchResult result = engine.SearchBestMove();

        Assert.Multiple(() =>
        {
            Assert.That(result.HasMove, Is.True);
            Assert.That(result.BestMove, Has.Length.GreaterThanOrEqualTo(4));
            Assert.That(engine.FEN, Is.EqualTo(before));
            Assert.That(engine.GetMoveHistory(), Has.Count.EqualTo(historyCount));
        });
    }

    [Test]
    public void SearchBestMove_ReportsEveryCompletedIteration()
    {
        var engine = new Engine(Kiwipete) { PlyDepthSearched = 3 };
        var depths = new List<int>();

        EngineSearchResult result = engine.SearchBestMove(
            CancellationToken.None,
            info => depths.Add(info.Depth));

        Assert.Multiple(() =>
        {
            Assert.That(depths, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(result.Info.Depth, Is.EqualTo(3));
            Assert.That(result.Info.PrincipalVariation, Is.EqualTo(result.BestMove));
        });
    }

    [Test]
    public void Cancellation_ReturnsTheLastCompletedIteration()
    {
        var engine = new Engine(Kiwipete) { PlyDepthSearched = 20 };
        using var cancellation = new CancellationTokenSource();

        EngineSearchResult result = engine.SearchBestMove(
            cancellation.Token,
            info =>
            {
                if (info.Depth == 2) cancellation.Cancel();
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.HasMove, Is.True);
            Assert.That(result.Info.Depth, Is.EqualTo(2));
            Assert.That(result.BestMove, Is.EqualTo(result.Info.PrincipalVariation));
        });
    }

    [Test]
    public void MateScore_ReportsTheCorrectUciDistance()
    {
        var engine = new Engine("k7/7R/6R1/8/8/8/8/K7 w - - 0 1")
        {
            PlyDepthSearched = 3
        };

        EngineSearchResult result = engine.SearchBestMove();

        Assert.Multiple(() =>
        {
            Assert.That(result.BestMove, Is.EqualTo("g6g8"));
            Assert.That(result.Info.Score, Is.EqualTo(EngineSearchInfo.MateScore - 1));
            Assert.That(result.Info.IsMate, Is.True);
            Assert.That(result.Info.MateInMoves, Is.EqualTo(1));
        });
    }

    [Test]
    public void LosingMateScore_UsesANegativeUciDistance()
    {
        var info = new EngineSearchInfo
        {
            // Two plies means the opponent mates on its next move.
            Score = -(EngineSearchInfo.MateScore - 2)
        };

        Assert.Multiple(() =>
        {
            Assert.That(info.IsMate, Is.True);
            Assert.That(info.MateInMoves, Is.EqualTo(-1));
        });
    }
}
