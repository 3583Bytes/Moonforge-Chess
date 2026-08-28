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


// ── Where progress lives ──────────────────────────────────────────────────────
// IndexedDB rather than localStorage: hundreds of megabytes instead of about five,
// which is the headroom a game archive and per-opening progress will need. Still
// entirely on the visitor's device — nothing here is sent anywhere.
//
// localStorage remains the fallback (private browsing can refuse IndexedDB) and the
// migration source, so anyone who already has a streak keeps it.
const CHESSBIN_DB = "chessbin";
const CHESSBIN_STORE = "data";

function chessBinDb() {
    return new Promise((resolve, reject) => {
        if (!window.indexedDB) return reject(new Error("no indexedDB"));
        const request = window.indexedDB.open(CHESSBIN_DB, 1);
        request.onupgradeneeded = () => {
            const db = request.result;
            if (!db.objectStoreNames.contains(CHESSBIN_STORE)) db.createObjectStore(CHESSBIN_STORE);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function chessBinTx(db, mode) {
    return db.transaction(CHESSBIN_STORE, mode).objectStore(CHESSBIN_STORE);
}

window.chessBinStore = {
    get: async key => {
        try {
            const db = await chessBinDb();
            const held = await new Promise((resolve, reject) => {
                const r = chessBinTx(db, "readonly").get(key);
                r.onsuccess = () => resolve(r.result ?? null);
                r.onerror = () => reject(r.error);
            });
            if (held !== null && held !== undefined) return held;

            // Nothing stored yet: adopt whatever localStorage still holds, once.
            const legacy = localStorage.getItem(key);
            if (legacy !== null) await window.chessBinStore.set(key, legacy);
            return legacy;
        } catch {
            try { return localStorage.getItem(key); } catch { return null; }
        }
    },

    set: async (key, value) => {
        try {
            const db = await chessBinDb();
            await new Promise((resolve, reject) => {
                const r = chessBinTx(db, "readwrite").put(value, key);
                r.onsuccess = () => resolve();
                r.onerror = () => reject(r.error);
            });
        } catch {
            // A browser that refuses both is a browser that keeps no progress; that is
            // the visitor's choice and must not break the game.
            try { localStorage.setItem(key, value); } catch { }
        }
    },

    all: async () => {
        const out = {};
        try {
            const db = await chessBinDb();
            const [keys, values] = await Promise.all([
                new Promise((res, rej) => { const r = chessBinTx(db, "readonly").getAllKeys(); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); }),
                new Promise((res, rej) => { const r = chessBinTx(db, "readonly").getAll(); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); }),
            ]);
            keys.forEach((k, i) => { out[k] = values[i]; });
        } catch {
            try {
                for (let i = 0; i < localStorage.length; i++) {
                    const k = localStorage.key(i);
                    if (k && k.startsWith("chessbin.")) out[k] = localStorage.getItem(k);
                }
            } catch { }
        }
        return JSON.stringify(out);
    },

    putAll: async json => {
        let entries;
        try { entries = JSON.parse(json); } catch { return false; }
        for (const [key, value] of Object.entries(entries)) {
            await window.chessBinStore.set(key, String(value));
        }
        return true;
    },
};

window.chessBin = {
    // A random name for this browser, made once and kept. It identifies a returning visitor
    // to the vote server without asking anyone to sign up for anything, and it is not tied to
    // a person — clearing site data gets you a new one, which is the visitor's right.
    getPlayerToken: async () => {
        let token = await window.chessBinStore.get("chessbin.token");
        if (typeof token === "string" && token.length >= 8) return token;

        // randomUUID needs a secure context; getRandomValues does not, so it covers the rest.
        if (crypto.randomUUID) {
            token = crypto.randomUUID().replace(/-/g, "");
        } else {
            const bytes = new Uint8Array(16);
            crypto.getRandomValues(bytes);
            token = Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
        }

        await window.chessBinStore.set("chessbin.token", token);
        return token;
    },

    copyText: async text => {
        if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text);
    },
    getSettings: () => window.chessBinStore.get("chessbin.settings"),
    saveSettings: value => window.chessBinStore.set("chessbin.settings", value),
    getPuzzleProgress: () => window.chessBinStore.get("chessbin.puzzle"),
    savePuzzleProgress: value => window.chessBinStore.set("chessbin.puzzle", value),
    exportProgress: () => window.chessBinStore.all(),
    importProgress: json => window.chessBinStore.putAll(json),
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
