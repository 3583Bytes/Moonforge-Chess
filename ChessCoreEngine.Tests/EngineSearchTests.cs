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
        var iterations = new List<EngineSearchInfo>();

        EngineSearchResult result = engine.SearchBestMove(
            CancellationToken.None,
            info => iterations.Add(info));

        Assert.Multiple(() =>
        {
            Assert.That(iterations.Select(info => info.Depth), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(iterations.Select(info => PvMoves(info).Length), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(result.Info.Depth, Is.EqualTo(3));
            Assert.That(PvMoves(result.Info), Has.Length.EqualTo(3));
            Assert.That(PvMoves(result.Info)[0], Is.EqualTo(result.BestMove));
        });
        foreach (EngineSearchInfo iteration in iterations)
            AssertPrincipalVariationIsLegal(Kiwipete, iteration);
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
            Assert.That(PvMoves(result.Info), Has.Length.EqualTo(2));
            Assert.That(PvMoves(result.Info)[0], Is.EqualTo(result.BestMove));
        });
        AssertPrincipalVariationIsLegal(Kiwipete, result.Info);
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
            Assert.That(result.Info.PrincipalVariation, Is.EqualTo("g6g8"));
        });
        Position final = ReplayPrincipalVariation(
            "k7/7R/6R1/8/8/8/8/K7 w - - 0 1", result.Info);
        var replies = new List<Move>();
        MoveGen.GenerateLegal(final, replies);
        Assert.Multiple(() =>
        {
            Assert.That(replies, Is.Empty);
            Assert.That(MoveGen.InCheck(final, final.SideToMove), Is.True);
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

    [Test]
    public void PromotionPrincipalVariation_UsesUciNotationAndIsLegal()
    {
        const string fen = "6nk/4Pppp/8/8/8/8/PPPPPPPP/K7 w - - 0 1";
        var engine = new Engine(fen) { PlyDepthSearched = 3 };

        engine.SearchBestMove(); // warm the TT so the second PV exercises reconstruction
        EngineSearchResult result = engine.SearchBestMove();

        Assert.Multiple(() =>
        {
            Assert.That(result.BestMove, Is.EqualTo("e7e8q"));
            Assert.That(PvMoves(result.Info)[0], Is.EqualTo("e7e8q"));
            Assert.That(PvMoves(result.Info), Has.Length.EqualTo(3));
        });
        AssertPrincipalVariationIsLegal(fen, result.Info);
    }

    private static string[] PvMoves(EngineSearchInfo info)
        => info.PrincipalVariation.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void AssertPrincipalVariationIsLegal(string fen, EngineSearchInfo info)
        => ReplayPrincipalVariation(fen, info);

    private static Position ReplayPrincipalVariation(string fen, EngineSearchInfo info)
    {
        Position position = Position.FromFen(fen);
        foreach (string notation in PvMoves(info))
        {
            var legalMoves = new List<Move>();
            MoveGen.GenerateLegal(position, legalMoves);
            Move? selected = null;
            foreach (Move move in legalMoves)
            {
                if (move.ToString() == notation)
                {
                    selected = move;
                    break;
                }
            }

            Assert.That(selected.HasValue, Is.True,
                $"PV move {notation} is illegal after replaying {info.PrincipalVariation}");
            position.MakeMove(selected!.Value);
        }
        return position;
    }
}
