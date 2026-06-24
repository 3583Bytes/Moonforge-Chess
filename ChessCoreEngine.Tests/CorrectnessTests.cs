using System;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using ChessEngine.Engine;

namespace ChessCoreEngine.Tests
{
	public class CorrectnessTest
	{
		public void RunAllCorrectnessTests()
		{
			TestBlankBoard();
			TestStandardBoardShortFEN();
			TestStandardBoardFullFEN();
			TestNotation();
			TestValidMoves();
			TestSimpleMoves();
			TestRealGame();
			Test50MoveRule();
			TestAI();
		}
		
		[Test]
		public void TestBlankBoard()
		{
			var board = new Board();
			var fen = Board.Fen(false, board);
			ClassicAssert.AreEqual("8/8/8/8/8/8/8/8 w - - 0 0", fen);
		}

		[Test]
		public void TestStandardBoardShortFEN()
		{
			var standardFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w - -";
			var board = new Board(standardFen);
			var fen = Board.Fen(true, board);
			ClassicAssert.AreEqual(standardFen, fen);
		}

		[Test]
		public void TestStandardBoardFullFEN()
		{
			var standardFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
			var board = new Board(standardFen);
			var fen = Board.Fen(false, board);
			ClassicAssert.AreEqual(standardFen, fen);
		}
		
		[Test]
		public void TestBoardOrientation()
		{
			var standardFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
			var board = new Board(standardFen);

			// 0,0 is a8
			// 7,7 is h1

			// is the board oriented the way we expect?
			var piece = board.Squares[0].Piece;
			ClassicAssert.AreEqual(ChessPieceColor.Black, piece.PieceColor);
			ClassicAssert.AreEqual(ChessPieceType.Rook, piece.PieceType);

			piece = board.Squares[3].Piece;
			ClassicAssert.AreEqual(ChessPieceColor.Black, piece.PieceColor);
			ClassicAssert.AreEqual(ChessPieceType.Queen, piece.PieceType);

			piece = board.Squares[63].Piece;
			ClassicAssert.AreEqual(ChessPieceColor.White, piece.PieceColor);
			ClassicAssert.AreEqual(ChessPieceType.Rook, piece.PieceType);

			piece = board.Squares[59].Piece;
			ClassicAssert.AreEqual(ChessPieceColor.White, piece.PieceColor);
			ClassicAssert.AreEqual(ChessPieceType.Queen, piece.PieceType);
		}
		
		[Test]
		public void TestNotation()
		{
			byte sourceColumn=0, sourceRow=0, destinationColumn=0, destinationRow=0;

			ClassicAssert.IsFalse(MoveContent.ParseAN("toolong", ref sourceColumn, ref sourceRow, ref destinationColumn, ref destinationRow));
			ClassicAssert.IsFalse(MoveContent.ParseAN("abc", ref sourceColumn, ref sourceRow, ref destinationColumn, ref destinationRow));

			ClassicAssert.IsTrue(MoveContent.ParseAN("a8h1", ref sourceColumn, ref sourceRow, ref destinationColumn, ref destinationRow));
			ClassicAssert.AreEqual(sourceColumn, 0);
			ClassicAssert.AreEqual(sourceRow, 0);
			ClassicAssert.AreEqual(destinationColumn, 7);
			ClassicAssert.AreEqual(destinationRow, 7);

			ClassicAssert.IsTrue(MoveContent.ParseAN("b3e4", ref sourceColumn, ref sourceRow, ref destinationColumn, ref destinationRow));
			ClassicAssert.AreEqual(sourceColumn, 1);
			ClassicAssert.AreEqual(sourceRow, 5);
			ClassicAssert.AreEqual(destinationColumn, 4);
			ClassicAssert.AreEqual(destinationRow, 4);
		}

		[Test]
		public void TestValidMoves()
		{
			var engine = new Engine("rnbqkbnr/ppppppp1/8/8/8/8/8/8 w KQkq - 0 1");
			
			// rook
			ClassicAssert.IsFalse(engine.IsValidMove(0,0,7,7));
			ClassicAssert.IsFalse(engine.IsValidMove(0,0,0,1));
			ClassicAssert.IsFalse(engine.IsValidMoveAN("a8g1"));
			ClassicAssert.IsFalse(engine.IsValidMoveAN("a8a7"));
			
			// pawn
			ClassicAssert.IsTrue(engine.IsValidMove(0,1,0,2));
			ClassicAssert.IsTrue(engine.IsValidMove(0,1,0,3));
			ClassicAssert.IsFalse(engine.IsValidMove(0,1,0,4));
			ClassicAssert.IsTrue(engine.IsValidMoveAN("a7a6"));
			ClassicAssert.IsTrue(engine.IsValidMoveAN("a7a5"));
			ClassicAssert.IsFalse(engine.IsValidMoveAN("a7a4"));
			
			// knight
			ClassicAssert.IsTrue(engine.IsValidMove(1,0,0,2));
			ClassicAssert.IsTrue(engine.IsValidMove(1,0,2,2));
			ClassicAssert.IsFalse(engine.IsValidMove(1,0,3,1));
			ClassicAssert.IsTrue(engine.IsValidMoveAN("b8a6"));
			ClassicAssert.IsTrue(engine.IsValidMoveAN("b8c6"));
			ClassicAssert.IsFalse(engine.IsValidMoveAN("b8d7"));

			// rook2
			ClassicAssert.IsTrue(engine.IsValidMove(7,0,7,1));
			ClassicAssert.IsTrue(engine.IsValidMove(7,0,7,7));
			ClassicAssert.IsFalse(engine.IsValidMove(7,0,6,7));
			ClassicAssert.IsTrue(engine.IsValidMoveAN("h8h7"));
			ClassicAssert.IsTrue(engine.IsValidMoveAN("h8h1"));
			ClassicAssert.IsFalse(engine.IsValidMoveAN("h8g1"));
		}
		
		[Test]
		public void TestSimpleMoves()
		{
			var standardFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
			var engine = new Engine(standardFen);
			
			engine.MovePieceAN("e2e4");
			ClassicAssert.AreEqual("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1", engine.FEN);

			engine.MovePieceAN("c7c5");
			ClassicAssert.AreEqual("rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2", engine.FEN);

			engine.MovePieceAN("g1f3");
			ClassicAssert.AreEqual("rnbqkbnr/pp1ppppp/8/2p5/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2", engine.FEN);
		}

		[Test]
		public void Test50MoveRule() {
			var engine = new Engine("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
			for (var i=0; i<24; i++) {
				engine.MovePieceAN("b1c3");
				engine.MovePieceAN("b8c6");
				engine.MovePieceAN("c3b1");
				engine.MovePieceAN("c6b8");
				ClassicAssert.IsFalse(engine.FiftyMove);
			}
			engine.MovePieceAN("b1c3");
			ClassicAssert.IsFalse(engine.FiftyMove);
			engine.MovePieceAN("b8c6");
			ClassicAssert.IsFalse(engine.FiftyMove);
			engine.MovePieceAN("c3b1");
			ClassicAssert.IsFalse(engine.FiftyMove);
			engine.MovePieceAN("c6b8");
			ClassicAssert.IsTrue(engine.FiftyMove);
		}
		
		[Test]
		public void TestRealGame() {
			/*
			// http://www.chessgames.com/perl/nph-chesspgn?text=1&gid=1067317
			string[] pgn = {
	"[Event \"Third Rosenwald Trophy\"]",
	"[Site \"New York, NY USA\"]",
	"[Date \"1956.10.17\"]",
	"[EventDate \"1956.10.07\"]",
	"[Round \"8\"]",
	"[Result \"0-1\"]",
	"[White \"Donald Byrne\"]",
	"[Black \"Robert James Fischer\"]",
	"[ECO \"D92\"]",
	"[WhiteElo \"?\"]",
	"[BlackElo \"?\"]",
	"[PlyCount \"82\"]",
	"",
	"1. Nf3 Nf6 2. c4 g6 3. Nc3 Bg7 4. d4 O-O 5. Bf4 d5 6. Qb3 dxc4",
	"7. Qxc4 c6 8. e4 Nbd7 9. Rd1 Nb6 10. Qc5 Bg4 11. Bg5 {11. Be2",
	"followed by 12. O-O would have been more prudent. The bishop",
	"move played allows a sudden crescendo of tactical points to be",
	"uncovered by Fischer. -- Wade} Na4 {!} 12. Qa3 {On 12. Nxa4",
	"Nxe4 and White faces considerable difficulties.} Nxc3 {At",
	"first glance, one might think that this move only helps White",
	"create a stronger pawn center; however, Fischer's plan is",
	"quite the opposite. By eliminating the Knight on c3, it",
	"becomes possible to sacrifice the exchange via Nxe4 and smash",
	"White's center, while the King remains trapped in the center.}",
	"13. bxc3 Nxe4 {The natural continuation of Black's plan.}",
	"14. Bxe7 Qb6 15. Bc4 Nxc3 16. Bc5 Rfe8+ 17. Kf1 Be6 {!! If",
	"this is the game of the century, then 17...Be6!! must be the",
	"counter of the century. Fischer offers his queen in exchange",
	"for a fierce attack with his minor pieces. Declining this",
	"offer is not so easy: 18. Bxe6 leads to a 'Philidor Mate'",
	"(smothered mate) with ...Qb5+ 19. Kg1 Ne2+ 20. Kf1 Ng3+",
	"21. Kg1 Qf1+ 22. Rxf1 Ne2#. Other ways to decline the queen",
	"also run into trouble: e.g., 18. Qxc3 Qxc5} 18. Bxb6 Bxc4+",
	"19. Kg1 Ne2+ 20. Kf1 Nxd4+ {This tactical scenario, where a",
	"king is repeatedly revealed to checks, is sometimes called a",
	"\"windmill.\"} 21. Kg1 Ne2+ 22. Kf1 Nc3+ 23. Kg1 axb6 24. Qb4",
	"Ra4 25. Qxb6 Nxd1 26. h3 Rxa2 27. Kh2 Nxf2 28. Re1 Rxe1",
	"29. Qd8+ Bf8 30. Nxe1 Bd5 31. Nf3 Ne4 32. Qb8 b5 {Every piece",
	"and pawn of the black camp is defended. The white queen has",
	"nothing to do.} 33. h4 h5 34. Ne5 Kg7 35. Kg1 Bc5+ 36. Kf1",
	"Ng3+ {Now Byrne is hopelessly entangled in Fischer's mating",
	"net.} 37. Ke1 Bb4+ 38. Kd1 Bb3+ 39. Kc1 Ne2+ 40. Kb1 Nc3+",
	"41. Kc1 Rc2# 0-1"
		};
			*/

			var engine = new Engine("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
			string[] movelist = {
				"g1f3",
				"g8f6",
				"c2c4",
				"g7g6",
				"b1c3",
				"f8g7",
				"d2d4",
				"e8g8",
				"c1f4",
				"d7d5",
				"d1b3",
				"d5c4",
				"b3c4",
				"c7c6",
				"e2e4",
				"b8d7",
				"a1d1",
				"d7b6",
				"c4c5",
				"c8g4",
				"f4g5",
				"b6a4",
				"c5a3",
				"a4c3",
				"b2c3",
				"f6e4",
				"g5e7",
				"d8b6",
				"f1c4",
				"e4c3",
				"e7c5",
				"f8e8",
				"e1f1",
				"g4e6",
				"c5b6",
				"e6c4",
				"f1g1",
				"c3e2",
				"g1f1",
				"e2d4",
				"f1g1",
				"d4e2",
				"g1f1",
				"e2c3",
				"f1g1",
				"a7b6",
				"a3b4",
				"a8a4",
				"b4b6",
				"c3d1",
				"h2h3",
				"a4a2",
				"g1h2",
				"d1f2",
				"h1e1",
				"e8e1",
				"b6d8",
				"g7f8",
				"f3e1",
				"c4d5",
				"e1f3",
				"f2e4",
				"d8b8",
				"b7b5",
				"h3h4",
				"h7h5",
				"f3e5",
				"g8g7",
				"h2g1",
				"f8c5",
				"g1f1",
				"e4g3",
				"f1e1",
				"c5b4",
				"e1d1",
				"d5b3",
				"d1c1",
				"g3e2",
				"c1b1",
				"e2c3",
				"b1c1",
				"a2c2"
			};
			string[] fenlist = {
				"rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R b KQkq - 1 1",
				"rnbqkb1r/pppppppp/5n2/8/8/5N2/PPPPPPPP/RNBQKB1R w KQkq - 2 2",
				"rnbqkb1r/pppppppp/5n2/8/2P5/5N2/PP1PPPPP/RNBQKB1R b KQkq c3 0 2",
				"rnbqkb1r/pppppp1p/5np1/8/2P5/5N2/PP1PPPPP/RNBQKB1R w KQkq - 0 3",
				"rnbqkb1r/pppppp1p/5np1/8/2P5/2N2N2/PP1PPPPP/R1BQKB1R b KQkq - 1 3",
				"rnbqk2r/ppppppbp/5np1/8/2P5/2N2N2/PP1PPPPP/R1BQKB1R w KQkq - 2 4",
				"rnbqk2r/ppppppbp/5np1/8/2PP4/2N2N2/PP2PPPP/R1BQKB1R b KQkq d3 0 4",
				"rnbq1rk1/ppppppbp/5np1/8/2PP4/2N2N2/PP2PPPP/R1BQKB1R w KQ - 1 5",
				"rnbq1rk1/ppppppbp/5np1/8/2PP1B2/2N2N2/PP2PPPP/R2QKB1R b KQ - 2 5",
				"rnbq1rk1/ppp1ppbp/5np1/3p4/2PP1B2/2N2N2/PP2PPPP/R2QKB1R w KQ d6 0 6",
				"rnbq1rk1/ppp1ppbp/5np1/3p4/2PP1B2/1QN2N2/PP2PPPP/R3KB1R b KQ - 1 6",
				"rnbq1rk1/ppp1ppbp/5np1/8/2pP1B2/1QN2N2/PP2PPPP/R3KB1R w KQ - 0 7",
				"rnbq1rk1/ppp1ppbp/5np1/8/2QP1B2/2N2N2/PP2PPPP/R3KB1R b KQ - 0 7",
				"rnbq1rk1/pp2ppbp/2p2np1/8/2QP1B2/2N2N2/PP2PPPP/R3KB1R w KQ - 0 8",
				"rnbq1rk1/pp2ppbp/2p2np1/8/2QPPB2/2N2N2/PP3PPP/R3KB1R b KQ e3 0 8",
				"r1bq1rk1/pp1nppbp/2p2np1/8/2QPPB2/2N2N2/PP3PPP/R3KB1R w KQ - 1 9",
				"r1bq1rk1/pp1nppbp/2p2np1/8/2QPPB2/2N2N2/PP3PPP/3RKB1R b K - 2 9",
				"r1bq1rk1/pp2ppbp/1np2np1/8/2QPPB2/2N2N2/PP3PPP/3RKB1R w K - 3 10",
				"r1bq1rk1/pp2ppbp/1np2np1/2Q5/3PPB2/2N2N2/PP3PPP/3RKB1R b K - 4 10",
				"r2q1rk1/pp2ppbp/1np2np1/2Q5/3PPBb1/2N2N2/PP3PPP/3RKB1R w K - 5 11",
				"r2q1rk1/pp2ppbp/1np2np1/2Q3B1/3PP1b1/2N2N2/PP3PPP/3RKB1R b K - 6 11",
				"r2q1rk1/pp2ppbp/2p2np1/2Q3B1/n2PP1b1/2N2N2/PP3PPP/3RKB1R w K - 7 12",
				"r2q1rk1/pp2ppbp/2p2np1/6B1/n2PP1b1/Q1N2N2/PP3PPP/3RKB1R b K - 8 12",
				"r2q1rk1/pp2ppbp/2p2np1/6B1/3PP1b1/Q1n2N2/PP3PPP/3RKB1R w K - 0 13",
				"r2q1rk1/pp2ppbp/2p2np1/6B1/3PP1b1/Q1P2N2/P4PPP/3RKB1R b K - 0 13",
				"r2q1rk1/pp2ppbp/2p3p1/6B1/3Pn1b1/Q1P2N2/P4PPP/3RKB1R w K - 0 14",
				"r2q1rk1/pp2Bpbp/2p3p1/8/3Pn1b1/Q1P2N2/P4PPP/3RKB1R b K - 0 14",
				"r4rk1/pp2Bpbp/1qp3p1/8/3Pn1b1/Q1P2N2/P4PPP/3RKB1R w K - 1 15",
				"r4rk1/pp2Bpbp/1qp3p1/8/2BPn1b1/Q1P2N2/P4PPP/3RK2R b K - 2 15",
				"r4rk1/pp2Bpbp/1qp3p1/8/2BP2b1/Q1n2N2/P4PPP/3RK2R w K - 0 16",
				"r4rk1/pp3pbp/1qp3p1/2B5/2BP2b1/Q1n2N2/P4PPP/3RK2R b K - 1 16",
				"r3r1k1/pp3pbp/1qp3p1/2B5/2BP2b1/Q1n2N2/P4PPP/3RK2R w K - 2 17",
				"r3r1k1/pp3pbp/1qp3p1/2B5/2BP2b1/Q1n2N2/P4PPP/3R1K1R b - - 3 17",
				"r3r1k1/pp3pbp/1qp1b1p1/2B5/2BP4/Q1n2N2/P4PPP/3R1K1R w - - 4 18",
				"r3r1k1/pp3pbp/1Bp1b1p1/8/2BP4/Q1n2N2/P4PPP/3R1K1R b - - 0 18",
				"r3r1k1/pp3pbp/1Bp3p1/8/2bP4/Q1n2N2/P4PPP/3R1K1R w - - 0 19",
				"r3r1k1/pp3pbp/1Bp3p1/8/2bP4/Q1n2N2/P4PPP/3R2KR b - - 1 19",
				"r3r1k1/pp3pbp/1Bp3p1/8/2bP4/Q4N2/P3nPPP/3R2KR w - - 2 20",
				"r3r1k1/pp3pbp/1Bp3p1/8/2bP4/Q4N2/P3nPPP/3R1K1R b - - 3 20",
				"r3r1k1/pp3pbp/1Bp3p1/8/2bn4/Q4N2/P4PPP/3R1K1R w - - 0 21",
				"r3r1k1/pp3pbp/1Bp3p1/8/2bn4/Q4N2/P4PPP/3R2KR b - - 1 21",
				"r3r1k1/pp3pbp/1Bp3p1/8/2b5/Q4N2/P3nPPP/3R2KR w - - 2 22",
				"r3r1k1/pp3pbp/1Bp3p1/8/2b5/Q4N2/P3nPPP/3R1K1R b - - 3 22",
				"r3r1k1/pp3pbp/1Bp3p1/8/2b5/Q1n2N2/P4PPP/3R1K1R w - - 4 23",
				"r3r1k1/pp3pbp/1Bp3p1/8/2b5/Q1n2N2/P4PPP/3R2KR b - - 5 23",
				"r3r1k1/1p3pbp/1pp3p1/8/2b5/Q1n2N2/P4PPP/3R2KR w - - 0 24",
				"r3r1k1/1p3pbp/1pp3p1/8/1Qb5/2n2N2/P4PPP/3R2KR b - - 1 24",
				"4r1k1/1p3pbp/1pp3p1/8/rQb5/2n2N2/P4PPP/3R2KR w - - 2 25",
				"4r1k1/1p3pbp/1Qp3p1/8/r1b5/2n2N2/P4PPP/3R2KR b - - 0 25",
				"4r1k1/1p3pbp/1Qp3p1/8/r1b5/5N2/P4PPP/3n2KR w - - 0 26",
				"4r1k1/1p3pbp/1Qp3p1/8/r1b5/5N1P/P4PP1/3n2KR b - - 0 26",
				"4r1k1/1p3pbp/1Qp3p1/8/2b5/5N1P/r4PP1/3n2KR w - - 0 27",
				"4r1k1/1p3pbp/1Qp3p1/8/2b5/5N1P/r4PPK/3n3R b - - 1 27",
				"4r1k1/1p3pbp/1Qp3p1/8/2b5/5N1P/r4nPK/7R w - - 0 28",
				"4r1k1/1p3pbp/1Qp3p1/8/2b5/5N1P/r4nPK/4R3 b - - 1 28",
				"6k1/1p3pbp/1Qp3p1/8/2b5/5N1P/r4nPK/4r3 w - - 0 29",
				"3Q2k1/1p3pbp/2p3p1/8/2b5/5N1P/r4nPK/4r3 b - - 1 29",
				"3Q1bk1/1p3p1p/2p3p1/8/2b5/5N1P/r4nPK/4r3 w - - 2 30",
				"3Q1bk1/1p3p1p/2p3p1/8/2b5/7P/r4nPK/4N3 b - - 0 30",
				"3Q1bk1/1p3p1p/2p3p1/3b4/8/7P/r4nPK/4N3 w - - 1 31",
				"3Q1bk1/1p3p1p/2p3p1/3b4/8/5N1P/r4nPK/8 b - - 2 31",
				"3Q1bk1/1p3p1p/2p3p1/3b4/4n3/5N1P/r5PK/8 w - - 3 32",
				"1Q3bk1/1p3p1p/2p3p1/3b4/4n3/5N1P/r5PK/8 b - - 4 32",
				"1Q3bk1/5p1p/2p3p1/1p1b4/4n3/5N1P/r5PK/8 w - b6 0 33",
				"1Q3bk1/5p1p/2p3p1/1p1b4/4n2P/5N2/r5PK/8 b - - 0 33",
				"1Q3bk1/5p2/2p3p1/1p1b3p/4n2P/5N2/r5PK/8 w - h6 0 34",
				"1Q3bk1/5p2/2p3p1/1p1bN2p/4n2P/8/r5PK/8 b - - 1 34",
				"1Q3b2/5pk1/2p3p1/1p1bN2p/4n2P/8/r5PK/8 w - - 2 35",
				"1Q3b2/5pk1/2p3p1/1p1bN2p/4n2P/8/r5P1/6K1 b - - 3 35",
				"1Q6/5pk1/2p3p1/1pbbN2p/4n2P/8/r5P1/6K1 w - - 4 36",
				"1Q6/5pk1/2p3p1/1pbbN2p/4n2P/8/r5P1/5K2 b - - 5 36",
				"1Q6/5pk1/2p3p1/1pbbN2p/7P/6n1/r5P1/5K2 w - - 6 37",
				"1Q6/5pk1/2p3p1/1pbbN2p/7P/6n1/r5P1/4K3 b - - 7 37",
				"1Q6/5pk1/2p3p1/1p1bN2p/1b5P/6n1/r5P1/4K3 w - - 8 38",
				"1Q6/5pk1/2p3p1/1p1bN2p/1b5P/6n1/r5P1/3K4 b - - 9 38",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1b4n1/r5P1/3K4 w - - 10 39",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1b4n1/r5P1/2K5 b - - 11 39",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1b6/r3n1P1/2K5 w - - 12 40",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1b6/r3n1P1/1K6 b - - 13 40",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1bn5/r5P1/1K6 w - - 14 41",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1bn5/r5P1/2K5 b - - 15 41",
				"1Q6/5pk1/2p3p1/1p2N2p/1b5P/1bn5/2r3P1/2K5 w - - 16 42"
			};
			var index=0;
			foreach (var move in movelist) {
				engine.MovePieceAN(move);
				ClassicAssert.AreEqual(fenlist[index], engine.FEN);
				index++;
			}
		}
		
		[Test]
		public void TestAI()
		{
			// set up a simple scenario with an obvious checkmate in 1 move
			var engine = new Engine("k7/7R/6R1/8/8/8/8/K7 w - - 0 1");
			engine.GameDifficulty = Engine.Difficulty.Easy;
			engine.AiPonderMove();
			MoveContent lastMove = engine.GetMoveHistory().ToArray()[0];
			string move = lastMove.GetPureCoordinateNotation();
			// did the AI find the checkmate?
			ClassicAssert.AreEqual("g6g8", move);
		}
	}
}
