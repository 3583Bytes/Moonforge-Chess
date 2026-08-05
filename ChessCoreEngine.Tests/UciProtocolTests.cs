using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ChessCore;
using NUnit.Framework;

namespace ChessCoreEngine.Tests;

[TestFixture]
[NonParallelizable]
public class UciProtocolTests
{
    private const string Kiwipete =
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    [Test]
    public void InfiniteSearch_ReportsProgress_AnswersReady_StopsOnce_AndDoesNotMove()
    {
        using var input = new BlockingLineReader();
        var output = new RecordingWriter();
        var protocol = new UciProtocol(input, output);
        Task run = Task.Run(protocol.Run);

        input.Add("position fen " + Kiwipete);
        input.Add("go infinite");
        Assert.That(output.WaitFor(line => line.StartsWith("info depth 1 "), 5000), Is.True,
            "search should publish its first completed iteration");

        input.Add("isready");
        Assert.That(output.WaitFor(line => line == "readyok", 1000), Is.True,
            "isready must be answered while search is active");

        input.Add("stop");
        Assert.That(output.WaitFor(line => line.StartsWith("bestmove "), 5000), Is.True);

        input.Add("fen");
        Assert.That(output.WaitFor(line => line == "info string " + Kiwipete, 1000), Is.True,
            "go must not apply its selected move");

        input.Add("quit");
        input.Complete();
        Assert.That(run.Wait(3000), Is.True);
        Assert.That(output.Lines.Count(line => line.StartsWith("bestmove ")), Is.EqualTo(1));
    }

    [Test]
    public void MateInOne_IsFormattedAsUciMateOne()
    {
        using var input = new BlockingLineReader();
        var output = new RecordingWriter();
        var protocol = new UciProtocol(input, output);
        Task run = Task.Run(protocol.Run);

        input.Add("position fen k7/7R/6R1/8/8/8/8/K7 w - - 0 1");
        input.Add("go depth 3");

        Assert.That(output.WaitFor(line => line.Contains(" score mate 1 "), 5000), Is.True);
        Assert.That(output.WaitFor(line => line == "bestmove g6g8", 5000), Is.True);

        input.Add("quit");
        input.Complete();
        Assert.That(run.Wait(3000), Is.True);
    }

    [Test]
    public void FixedDepthSearch_EmitsTheFullPrincipalVariation()
    {
        using var input = new BlockingLineReader();
        var output = new RecordingWriter();
        var protocol = new UciProtocol(input, output);
        Task run = Task.Run(protocol.Run);

        input.Add("position fen " + Kiwipete);
        input.Add("go depth 3");

        Assert.That(output.WaitFor(line => line.StartsWith("info depth 3 "), 5000), Is.True);
        Assert.That(output.WaitFor(line => line.StartsWith("bestmove "), 5000), Is.True);

        string depthThree = output.Lines.Last(line => line.StartsWith("info depth 3 "));
        string bestMove = output.Lines.Last(line => line.StartsWith("bestmove ")).Substring(9);
        int pvIndex = depthThree.IndexOf(" pv ", StringComparison.Ordinal);
        Assert.That(pvIndex, Is.GreaterThanOrEqualTo(0));
        string[] pv = depthThree.Substring(pvIndex + 4)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Multiple(() =>
        {
            Assert.That(pv, Has.Length.EqualTo(3));
            Assert.That(pv[0], Is.EqualTo(bestMove));
        });

        input.Add("quit");
        input.Complete();
        Assert.That(run.Wait(3000), Is.True);
    }

    [Test]
    public void QuitDuringSearch_CancelsAndReturnsOneBestMove()
    {
        using var input = new BlockingLineReader();
        var output = new RecordingWriter();
        var protocol = new UciProtocol(input, output);
        Task run = Task.Run(protocol.Run);

        input.Add("position fen " + Kiwipete);
        input.Add("go infinite");
        Assert.That(output.WaitFor(line => line.StartsWith("info depth 1 "), 5000), Is.True);

        input.Add("quit");
        input.Complete();

        Assert.That(run.Wait(5000), Is.True);
        Assert.That(output.Lines.Count(line => line.StartsWith("bestmove ")), Is.EqualTo(1));
    }

    private sealed class BlockingLineReader : TextReader
    {
        private readonly BlockingCollection<string> _lines = new();

        internal void Add(string line) => _lines.Add(line);
        internal void Complete() => _lines.CompleteAdding();

        public override string? ReadLine()
        {
            try
            {
                return _lines.Take();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _lines.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public override Encoding Encoding => Encoding.UTF8;
        internal string[] Lines => _lines.ToArray();

        public override void WriteLine(string? value)
        {
            if (value != null) _lines.Enqueue(value);
        }

        internal bool WaitFor(Func<string, bool> predicate, int timeoutMs)
        {
            var timer = Stopwatch.StartNew();
            return SpinWait.SpinUntil(
                () => _lines.Any(predicate),
                Math.Max(1, timeoutMs - (int)timer.ElapsedMilliseconds));
        }
    }
}
