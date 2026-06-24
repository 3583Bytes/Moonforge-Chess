# Changelog

All notable changes to this package are documented here.

## [1.2.0] - 2026-06-24

### Added
- Initial Unity (UPM) package wrapping the Moonforge Chess engine as a .NET Standard
  2.1 managed plugin (`Runtime/MoonforgeChess.Engine.dll`).
- Public `Engine` API for FEN setup, move application/validation (long algebraic
  notation), and AI play (`AiPonderMove`) with selectable difficulty.

### Changed
- Engine rewritten on a bitboard core (make/unmake, incremental Zobrist) — substantially
  faster and stronger, with correct en passant and underpromotion.
