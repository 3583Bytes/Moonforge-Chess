// Production service worker.
//
// ChessBin already runs entirely on the device — the engine, the analysis and the puzzle
// checking are all local — so the only thing standing between it and working offline was
// the download. This precaches the app shell and serves it from cache thereafter.

self.importScripts('./service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cachePrefix = 'chessbin-';
const cacheName = `${cachePrefix}${self.assetsManifest.version}`;

// The shell: everything needed to boot and render without the network.
const precacheInclude = [/\.wasm$/, /\.js$/, /\.css$/, /\.html$/, /\.json$/, /\.webmanifest$/,
                         /\.png$/, /\.ico$/, /\.dat$/];
// The worker itself must never precache itself, and the puzzle set is nearly a megabyte —
// far too much to force on every install. Shards are cached on demand instead (see onFetch).
// The community game's state changes on a schedule, so precaching it would show a stale
// board to anyone who had installed the app. Fetched from the network every time instead.
const precacheExclude = [/^service-worker\.js$/, /^puzzles\/shard-/, /^vote\/state\.json$/];

async function onInstall() {
    const assets = self.assetsManifest.assets
        .filter(asset => precacheInclude.some(p => p.test(asset.url)))
        .filter(asset => !precacheExclude.some(p => p.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

    await (await caches.open(cacheName)).addAll(assets);
}

async function onActivate() {
    // Drop caches from earlier deploys so a returning visitor isn't holding two copies of a
    // multi-megabyte runtime.
    const stale = (await caches.keys()).filter(key => key.startsWith(cachePrefix) && key !== cacheName);
    await Promise.all(stale.map(key => caches.delete(key)));
}

/// Maps a route to the boot page that serves it: "/" -> index.html,
/// "/puzzle" and "/puzzle/" -> puzzle/index.html.
function bootPageFor(pathname) {
    let path = pathname.replace(/^\//, '');
    if (path === '') return 'index.html';
    if (path.includes('.')) return path;
    return path.replace(/\/?$/, '/') + 'index.html';
}

async function onFetch(event) {
    const request = event.request;

    if (request.method !== 'GET') {
        return fetch(request);
    }

    const url = new URL(request.url);
    const sameOrigin = url.origin === self.location.origin;

    // Navigations are served from the cache, which is also what makes a deep link work
    // offline without the 404.html round trip. Prefer the route's *own* boot page so the
    // content shown before Blazor starts matches where the visitor is going — falling back
    // to index.html only for routes that have no boot page of their own.
    if (request.mode === 'navigate' && sameOrigin) {
        const own = await caches.match(bootPageFor(url.pathname));
        if (own) return own;

        const shell = await caches.match('index.html');
        if (shell) return shell;
    }

    // The community board is the one thing here that changes without a code change.
    if (sameOrigin && url.pathname.startsWith('/vote/')) {
        try {
            return await fetch(request, { cache: 'no-store' });
        } catch {
            const stale = await caches.match(request);
            return stale ?? new Response('', { status: 504, statusText: 'Offline' });
        }
    }

    // Puzzle shards are immutable once generated, so once seen they never need refetching.
    // Cache-on-first-use keeps the install light while still making yesterday's puzzle work
    // on a train today.
    if (sameOrigin && /\/puzzles\/.*\.json$/.test(url.pathname)) {
        const cache = await caches.open(cacheName);
        const hit = await cache.match(request);
        if (hit) return hit;
        try {
            const response = await fetch(request);
            if (response.ok) cache.put(request, response.clone());
            return response;
        } catch {
            return new Response('', { status: 504, statusText: 'Offline and this puzzle was never loaded' });
        }
    }

    const cached = await caches.match(request);
    if (cached) return cached;

    try {
        return await fetch(request);
    } catch (error) {
        // Offline and not in the cache: let the caller see a failure rather than a hang.
        return new Response('', { status: 504, statusText: 'Offline' });
    }
}
