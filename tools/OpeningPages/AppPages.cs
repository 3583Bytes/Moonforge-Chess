namespace ChessBin.Tools.OpeningPages;

/// <summary>
/// The seven boot pages for the app's own routes. These used to be hand-maintained copies of
/// each other, which is how the nav drifted between them; the chrome now comes from one
/// template and only the copy lives here.
/// <para>
/// Changing anything in this file changes what a crawler and a link preview see, and the
/// generator's output must stay byte-identical to what is committed unless the copy itself
/// changed — that diff is the check that the template still renders the site's chrome.
/// </para>
/// </summary>
internal static class AppPages
{
    internal static readonly AppPage[] All =
    [
        new AppPage
        {
            File = "index.html",
            Route = "/",
            Active = "/",
            Comment = "    The markup inside #app is deliberately real content, not a spinner: it is the only\n    thing search engines can read (they do not run WebAssembly) and it is what a visitor\n    looks at while the runtime downloads. Blazor replaces it once the app renders.\n    Shared behaviour lives in js/chessbin.js so this file and the other boot pages\n    cannot drift apart.",
            Description = "Play chess free against the computer, solve a puzzle a day, and have your games explained move by move — all in your browser. No signup, nothing to install.",
            Title = "Play Chess Online Free — vs the Computer | ChessBin",
            OgTitle = "ChessBin · Play chess free online",
            OgDescription = "Play chess free against the computer, solve a puzzle a day, and have your games explained move by move — all in your browser. No signup, nothing to install.",
            OgImage = "brand/og-image.png",
            OgImageAlt = "The ChessBin mark: a black knight and gold pawns against a blue triangle.",
            LdName = "ChessBin",
            LdDescription = "Play chess free against the computer, solve a daily puzzle, and get your games analysed — all in the browser.",
            LdBrowserRequirements = "Requires WebAssembly",
            Eyebrow = "Free · no signup · nothing to install",
            H1 = "Play chess. Find out why you lost.",
            Intro = "Play a game, solve today's puzzle, or paste a game from anywhere and have it explained. All free, all in your browser.",
            Note = "Everything runs on your device, so your games never leave the browser and it works with no connection once you have visited. When a game ends you get a review that says what each move cost and why — the part most sites charge for.",
            Links = [("/puzzle/", "Today's chess puzzle"), ("/puzzle/practice/", "Practice puzzles"), ("/review/", "Analyse a game"), ("/vote/", "Vote chess")],
        },
        new AppPage
        {
            File = "play-online/index.html",
            Route = "/play-online/",
            Active = "/play-online/",
            Comment = "    Boot page for /play-online. Same reasoning as the other routes: a real 200 response, its own\n    title and social card, and content a crawler can read without executing WebAssembly.",
            Description = "Play chess online against another person, free and with no signup. Get matched on your time control and play straight away in the browser.",
            Title = "Play Chess Online Against a Person | ChessBin",
            OgTitle = "ChessBin · Play chess online",
            OgDescription = "Play chess online against another person, free and with no signup. Get matched on your time control and play straight away in the browser.",
            OgImage = "brand/og-image.png",
            OgImageAlt = "The ChessBin mark: a black knight and gold pawns against a blue triangle.",
            LdName = "ChessBin Online Chess",
            LdDescription = "Play chess online against another person in the browser, with no account.",
            LdBrowserRequirements = null,
            Eyebrow = "Online chess · free · no signup",
            H1 = "Play someone.",
            Intro = "Get matched with whoever else is looking, and play. No account, no rating, nothing to install — just a game.",
            Note = "Pick a time control and you are paired with the next person who picks the same one. Both browsers check every move with the same engine that runs the rest of the site, so an illegal move cannot be played against you. There are no accounts and no ratings, so nothing about you is stored and there is nothing to win by cheating.",
            Links = [("/", "Play the computer"), ("/puzzle/", "Today&#39;s puzzle"), ("/openings/", "Opening explorer")],
        },
        new AppPage
        {
            File = "puzzle/index.html",
            Route = "/puzzle/",
            Active = "/puzzle/",
            Comment = "    Boot page for /puzzle. GitHub Pages serves this as the directory index, which gives\n    the route a real 200 response, its own title and social card, and crawlable content —\n    none of which the WebAssembly app can provide, since crawlers never execute it.",
            Description = "A new chess puzzle every day, free and the same for everyone. Solve it in your browser, then see exactly how the engine scores the position.",
            Title = "Daily Chess Puzzle — A New One Every Day | ChessBin",
            OgTitle = "ChessBin · Daily chess puzzle",
            OgDescription = "A new chess puzzle every day, free and the same for everyone. Solve it in your browser, then see exactly how the engine scores the position.",
            OgImage = "brand/og-puzzle.png",
            OgImageAlt = "The ChessBin mark above a rank of chessboard squares.",
            LdName = "ChessBin Daily Chess Puzzle",
            LdDescription = "A new chess puzzle every day, the same one for everyone, solved in your browser.",
            LdBrowserRequirements = null,
            Eyebrow = "A new puzzle every day",
            H1 = "Today's chess puzzle.",
            Intro = "One position a day, the same for everyone, taken from real games. No account — your streak lives in this browser.",
            Note = "Find the winning line, then see exactly how the engine scores the position — which is the part other puzzle sites leave out. Solve it without a wrong move to keep your streak.",
            Links = [("/puzzle/practice/", "Practice more puzzles"), ("/", "Play a full game")],
        },
        new AppPage
        {
            File = "puzzle/practice/index.html",
            Route = "/puzzle/practice/",
            Active = "/puzzle/",
            Comment = "    Boot page for /puzzle/practice. Without it the route fell through to 404.html, which\n    works in a browser but answers 404 to crawlers, link previews and anything that cannot\n    run JavaScript.",
            Description = "Practise chess tactics with unlimited puzzles by difficulty, free and in your browser. See the engine's evaluation after every solve.",
            Title = "Chess Tactics Practice — Unlimited Puzzles | ChessBin",
            OgTitle = "ChessBin · Chess tactics practice",
            OgDescription = "Practise chess tactics with unlimited puzzles by difficulty, free and in your browser. See the engine's evaluation after every solve.",
            OgImage = "brand/og-puzzle.png",
            OgImageAlt = "The ChessBin mark above a rank of chessboard squares.",
            LdName = "ChessBin Chess Puzzle Practice",
            LdDescription = "Unlimited chess tactics puzzles by difficulty, solved in your browser.",
            LdBrowserRequirements = null,
            Eyebrow = "As many as you want",
            H1 = "Chess tactics practice.",
            Intro = "Puzzles from the same set as the daily, as many as you like, filtered by difficulty. Nothing here touches your daily streak.",
            Note = "Pick easy, medium or hard and keep going. Each solve shows the engine's read on what changed, so you can see why the line works rather than just being told it does.",
            Links = [("/puzzle/", "Today's puzzle"), ("/", "Play a full game")],
        },
        new AppPage
        {
            File = "openings/index.html",
            Route = "/openings/",
            Active = "/openings/",
            Comment = "    Boot page for /openings. Same reasoning as the other routes: a real 200 response, its own\n    title and social card, and content a crawler can read without executing WebAssembly.",
            Description = "Explore every named chess opening move by move, with ECO codes and how often each line is played. Free, in your browser, no signup.",
            Title = "Chess Opening Explorer — Every Named Opening | ChessBin",
            OgTitle = "ChessBin · Chess opening explorer",
            OgDescription = "Explore every named chess opening move by move, with ECO codes and how often each line is played. Free, in your browser, no signup.",
            OgImage = "brand/og-puzzle.png",
            OgImageAlt = "The ChessBin mark above a rank of chessboard squares.",
            LdName = "ChessBin Opening Explorer",
            LdDescription = "Walk every named chess opening move by move, with ECO codes, in the browser.",
            LdBrowserRequirements = null,
            Eyebrow = "Opening explorer · free · no signup",
            H1 = "Where does this line go?",
            Intro = "Walk the openings move by move and see what every line is called. Names and ECO codes for the whole of published theory — no account, and it works offline.",
            Note = "Start from the first move and pick a continuation: each one shows what it is called, its ECO code, and how often the engine's book sees it played. Reach the same position by a different move order and you get the same entry, so transpositions resolve on their own. Any position can be carried straight onto the board to play out.",
            Links = [("/puzzle/", "Today&#39;s puzzle"), ("/", "Play a game"), ("/review/", "Analyse a game")],
        },
        new AppPage
        {
            File = "review/index.html",
            Route = "/review/",
            Active = "/review/",
            Comment = "    Boot page for /review. Same reasoning as the other routes: a real 200 response, its own\n    title and social card, and content a crawler can read without executing WebAssembly.",
            Description = "Paste a game from Lichess, Chess.com or your own notation and see what every move cost — and which part of the position paid for it. Free, in your browser, no signup.",
            Title = "Chess Game Analysis — Paste a PGN | ChessBin",
            OgTitle = "ChessBin · Chess game analysis",
            OgDescription = "Paste a game from Lichess, Chess.com or your own notation and see what every move cost — and which part of the position paid for it. Free, in your browser, no signup.",
            OgImage = "brand/og-puzzle.png",
            OgImageAlt = "The ChessBin mark above a rank of chessboard squares.",
            LdName = "ChessBin Game Analysis",
            LdDescription = "Free chess game analysis in the browser: paste a game and see what each move cost you, and why.",
            LdBrowserRequirements = null,
            Eyebrow = "Game analysis · free · no signup",
            H1 = "Why did that move lose?",
            Intro = "Paste a game from Lichess, Chess.com or your own notation and see what each move cost — and which part of the position paid for it.",
            Note = "Every move gets a verdict and a reason, not just a number: a review names what the move gave away — material, king safety, pawn structure or piece activity — and opens on the moment the game turned. Nothing you paste leaves your browser.",
            Links = [("/puzzle/", "Today&#39;s puzzle"), ("/", "Play the engine")],
        },
        new AppPage
        {
            File = "vote/index.html",
            Route = "/vote/",
            Active = "/vote/",
            Comment = "    Boot page for /vote. Same reasoning as the other routes: a real 200 response, its own\n    title and social card, and content a crawler can read without executing WebAssembly.",
            Description = "Everyone votes on one move a day against the Moonforge engine. Vote on the site — no account, no signup, nothing to install.",
            Title = "Vote Chess — Everyone vs the Engine | ChessBin",
            OgTitle = "ChessBin · Vote chess",
            OgDescription = "Everyone votes on one move a day against the Moonforge engine. Vote on the site — no account, no signup, nothing to install.",
            OgImage = "brand/og-puzzle.png",
            OgImageAlt = "The ChessBin mark above a rank of chessboard squares.",
            LdName = "ChessBin Vote Chess",
            LdDescription = "A community chess game: everyone votes on one move a day, and the Moonforge engine answers. No account needed to vote.",
            LdBrowserRequirements = null,
            Eyebrow = "Community game · free · no signup",
            H1 = "Everyone versus the engine.",
            Intro = "One move a day, chosen by whoever turns up. The move with the most votes gets played, and Moonforge answers.",
            Note = "Every legal move is on the ballot, so the community can play anything the rules allow — not a shortlist someone picked. One vote each, and you can change your mind until the deadline. Nothing is stored about you: your browser gets a random name the first time you vote, and clearing your site data throws it away.",
            Links = [("/puzzle/", "Today&#39;s puzzle"), ("/", "Play the engine")],
        },
    ];
}

internal sealed class AppPage
{
    public required string File, Route, Active, Comment, Description, Title;
    public required string OgTitle, OgDescription, OgImage, OgImageAlt;
    public required string LdName, LdDescription;
    public required string? LdBrowserRequirements;
    public required string Eyebrow, H1, Intro, Note;
    public required (string Href, string Text)[] Links;
}
