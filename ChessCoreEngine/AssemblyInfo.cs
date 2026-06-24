using System.Runtime.CompilerServices;

// Lets the test project exercise the internal bitboard core (Bitboards, Position,
// move generator) directly, rather than only through the public Engine façade.
[assembly: InternalsVisibleTo("ChessCoreEngine.Tests")]
