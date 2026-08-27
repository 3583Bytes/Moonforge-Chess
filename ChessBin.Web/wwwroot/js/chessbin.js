// Shared by every boot page (index.html and puzzle/index.html). Loading this from a
// file rather than inlining it keeps those pages from drifting apart.

// Restore the route 404.html stashed, before Blazor reads the URL. Only bare paths are
// accepted, so a crafted value can't turn this into an open redirect.
(function () {
    var target = sessionStorage.getItem("chessbin.redirect");
    if (!target) return;
    sessionStorage.removeItem("chessbin.redirect");
    if (target.charAt(0) === "/" && target.charAt(1) !== "/" && target !== "/") {
        history.replaceState(null, "", target);
    }
})();

// Registered from every boot page, at the root so its scope covers all routes. Failure is
// non-fatal: without a worker the site simply behaves as it did before, online only.
if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => {
        navigator.serviceWorker.register('/service-worker.js').catch(() => { });
    });
}

// Created on demand and reused. Browsers also start contexts suspended until a gesture,
// so resume it whenever we are about to make a noise.
window.chessBinAudio = () => {
    const Context = window.AudioContext || window.webkitAudioContext;
    if (!Context) return null;
    if (!window.__chessBinCtx) window.__chessBinCtx = new Context();
    if (window.__chessBinCtx.state === "suspended") window.__chessBinCtx.resume();
    return window.__chessBinCtx;
};

window.chessBin = {
    copyText: async text => {
        if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text);
    },
    getSettings: () => localStorage.getItem("chessbin.settings"),
    saveSettings: value => localStorage.setItem("chessbin.settings", value),
    getPuzzleProgress: () => localStorage.getItem("chessbin.puzzle"),
    savePuzzleProgress: value => localStorage.setItem("chessbin.puzzle", value),
    getFenFromUrl: () => new URLSearchParams(window.location.search).get("fen"),
    shareFen: async fen => {
        const url = new URL(window.location.href);
        url.searchParams.set("fen", fen);
        window.history.replaceState({}, "", url);
        if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(url.href);
    },
    // One shared context. The previous version built a new AudioContext for every move,
    // which browsers cap — after a handful of moves the sound simply stopped.
    playSound: kind => {
        const context = window.chessBinAudio();
        if (!context) return;

        const now = context.currentTime;
        const note = (frequency, at, length, peak, type) => {
            const oscillator = context.createOscillator();
            const gain = context.createGain();
            oscillator.type = type || "triangle";
            oscillator.frequency.value = frequency;
            gain.gain.setValueAtTime(0.0001, now + at);
            gain.gain.exponentialRampToValueAtTime(peak, now + at + 0.012);
            gain.gain.exponentialRampToValueAtTime(0.0001, now + at + length);
            oscillator.connect(gain).connect(context.destination);
            oscillator.start(now + at);
            oscillator.stop(now + at + length + 0.02);
        };

        switch (kind) {
            case "capture":                      // lower and blunter, so it lands
                note(150, 0, 0.16, 0.075, "square");
                note(90, 0.01, 0.13, 0.05);
                break;
            case "check":                        // rising, asks for attention
                note(660, 0, 0.09, 0.05);
                note(990, 0.07, 0.13, 0.05);
                break;
            case "mate":                         // falling and final
                note(523, 0, 0.16, 0.055);
                note(415, 0.14, 0.16, 0.055);
                note(311, 0.28, 0.34, 0.06);
                break;
            case "castle":                       // two clicks, one for each piece
                note(300, 0, 0.05, 0.045);
                note(300, 0.08, 0.06, 0.045);
                break;
            default:                             // a quiet move should stay quiet
                note(330, 0, 0.06, 0.04);
                break;
        }
    }
};
