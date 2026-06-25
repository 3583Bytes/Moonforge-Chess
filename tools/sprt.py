#!/usr/bin/env python3
"""SPRT match harness for Moonforge Chess.

Plays engine1 vs engine2 (UCI) under a Sequential Probability Ratio Test and stops
as soon as the result is statistically decided — so a change can be accepted or
rejected with far fewer games than a fixed-length match.

Requires: python-chess  (pip install chess) and two UCI engine commands.

Hypotheses (Elo of engine1 relative to engine2):
  H0: difference == --elo0   (default 0  → "no improvement")
  H1: difference == --elo1   (default +8 → "a real gain")
Accept H1  => engine1 is stronger; keep the change.
Accept H0  => the change is not an improvement; reject it.

Examples:
  # New build vs the committed baseline (build both first), 100ms/move:
  python3 tools/sprt.py \
      --engine1 "dotnet /tmp/new/ChessCore.dll" \
      --engine2 "dotnet /tmp/old/ChessCore.dll" \
      --movetime 0.1

  # vs Stockfish capped to a target strength:
  python3 tools/sprt.py \
      --engine1 "dotnet ChessCore/bin/Release/net10.0/ChessCore.dll" \
      --engine2 stockfish --sf-elo 1950 --movetime 0.2
"""
import argparse, math, shlex
import chess, chess.engine

# Varied, roughly balanced opening positions for game variety (each played twice,
# colours swapped).
OPENINGS = [
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2",
    "rnbqkbnr/ppp1pppp/8/3p4/3P4/8/PPP1PPPP/RNBQKBNR w KQkq d6 0 2",
    "rnbqkb1r/pppppppp/5n2/8/3P4/8/PPP1PPPP/RNBQKBNR w KQkq - 1 2",
    "rnbqkbnr/pp2pppp/2p5/3p4/2PP4/8/PP2PPPP/RNBQKBNR w KQkq - 0 3",
    "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3",
    "rnbqkb1r/pp2pppp/2p2n2/3p4/2PP4/2N5/PP2PPPP/R1BQKBNR w KQkq - 0 4",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
]


def expected(elo):
    return 1.0 / (1.0 + 10 ** (-elo / 400.0))


def play(e1, e2, fen, e1_white, limit, max_plies):
    board = chess.Board(fen)
    white, black = (e1, e2) if e1_white else (e2, e1)
    g = object()  # distinct object => python-chess issues ucinewgame
    while not board.is_game_over(claim_draw=True) and board.ply() < max_plies:
        eng = white if board.turn == chess.WHITE else black
        res = eng.play(board, limit, game=g)
        if res.move is None:
            break
        board.push(res.move)
    o = board.outcome(claim_draw=True)
    if o is None or o.winner is None:
        return 0.5
    return 1.0 if (o.winner == chess.WHITE) == e1_white else 0.0


def main():
    ap = argparse.ArgumentParser(description="SPRT match harness")
    ap.add_argument("--engine1", required=True)
    ap.add_argument("--engine2", required=True)
    ap.add_argument("--movetime", type=float, default=0.1, help="seconds per move")
    ap.add_argument("--elo0", type=float, default=0.0)
    ap.add_argument("--elo1", type=float, default=8.0)
    ap.add_argument("--alpha", type=float, default=0.05)
    ap.add_argument("--beta", type=float, default=0.05)
    ap.add_argument("--max-games", type=int, default=2000)
    ap.add_argument("--max-plies", type=int, default=200)
    ap.add_argument("--sf-elo", type=int, default=0,
                    help="if engine2 is Stockfish, cap it to this UCI_Elo")
    args = ap.parse_args()

    lower = math.log(args.beta / (1 - args.alpha))
    upper = math.log((1 - args.beta) / args.alpha)
    p0, p1 = expected(args.elo0), expected(args.elo1)

    e1 = chess.engine.SimpleEngine.popen_uci(shlex.split(args.engine1))
    e2 = chess.engine.SimpleEngine.popen_uci(shlex.split(args.engine2))
    if args.sf_elo:
        e2.configure({"UCI_LimitStrength": True, "UCI_Elo": args.sf_elo})

    limit = chess.engine.Limit(time=args.movetime)
    w = d = l = 0
    n = 0
    S = 0.0
    sumsq = 0.0
    verdict = "max games reached — no decision"
    try:
        gi = 0
        while n < args.max_games:
            fen = OPENINGS[(gi // 2) % len(OPENINGS)]
            e1_white = (gi % 2 == 0)
            r = play(e1, e2, fen, e1_white, limit, args.max_plies)
            n += 1; gi += 1
            if r == 1.0: w += 1
            elif r == 0.0: l += 1
            else: d += 1
            S += r; sumsq += r * r

            # Normalized (Brownian-motion) SPRT log-likelihood ratio.
            if n >= 2:
                var = sumsq / n - (S / n) ** 2
                # Floor the variance: only binds in lopsided mismatches (near-zero
                # sample variance) to keep the LLR finite; for close A/B matches the
                # real variance (~0.2) dominates, so this has no effect there.
                var = max(var, 0.01)
                llr = (p1 - p0) / var * (S - n * (p0 + p1) / 2.0)
            else:
                llr = 0.0

            print(f"g{n:4d}  W={w} L={l} D={d}  LLR={llr:+.2f}  bounds[{lower:.2f}, {upper:.2f}]", flush=True)

            if llr >= upper:
                verdict = "H1 ACCEPTED — engine1 is stronger; keep the change."
                break
            if llr <= lower:
                verdict = "H0 ACCEPTED — not an improvement; reject the change."
                break
    finally:
        e1.quit(); e2.quit()

    print("\n=== SPRT result ===")
    print(verdict)
    print(f"games={n}  W={w} L={l} D={d}  score={S}/{n} = {100*S/max(n,1):.1f}%")
    if 0 < S < n:
        p = S / n
        print(f"point estimate Elo(engine1 - engine2) = {-400*math.log10(1/p - 1):+.0f}")


if __name__ == "__main__":
    main()
