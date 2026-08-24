// Development build: deliberately inert.
//
// A caching worker during development means edits appear at random, so this one registers,
// claims nothing, and gets replaced at publish time by service-worker.published.js.
self.addEventListener('fetch', () => { });
