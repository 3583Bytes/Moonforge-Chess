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
    playMoveSound: () => {
        const Context = window.AudioContext || window.webkitAudioContext;
        if (!Context) return;
        const context = new Context();
        const oscillator = context.createOscillator();
        const gain = context.createGain();
        oscillator.frequency.value = 430;
        gain.gain.setValueAtTime(0.035, context.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.001, context.currentTime + 0.08);
        oscillator.connect(gain).connect(context.destination);
        oscillator.start();
        oscillator.stop(context.currentTime + 0.08);
    }
};
