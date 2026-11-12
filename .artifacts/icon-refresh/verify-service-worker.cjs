const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const test = require('node:test');
const source = fs.readFileSync('src/Feedarr.Web/feedarr-web/public/service-worker.js', 'utf8');
const version = '9d1d5be2bd5c';
function harness(scope) {
  const origin = 'https://feedarr.test';
  const listeners = {};
  const stores = new Map([['feedarr-pwa-v4', new Map()], ['unrelated-cache', new Map()]]);
  let online = true;
  const key = request => new URL(typeof request === 'string' ? request : request.url, origin).href;
  const caches = {
    keys: async () => [...stores.keys()], delete: async name => stores.delete(name),
    open: async name => {
      if (!stores.has(name)) stores.set(name, new Map());
      const data = stores.get(name);
      return {
        addAll: async paths => { for (const path of paths) data.set(key(path), new Response('precache')); },
        put: async (request, response) => { data.set(key(request), response.clone()); },
      };
    },
    match: async request => {
      for (const data of stores.values()) if (data.has(key(request))) return data.get(key(request)).clone();
    },
  };
  vm.runInNewContext(source, {
    self: { registration: { scope: origin + scope }, location: { origin },
      addEventListener: (name, fn) => { listeners[name] = fn; }, skipWaiting: async () => {}, clients: { claim: async () => {} } },
    caches, URL, Set, Promise,
    fetch: async () => { if (!online) throw new Error('offline'); return new Response('network'); },
  });
  return {
    stores, offline: () => { online = false; },
    lifecycle: async name => { let promise; listeners[name]({ waitUntil: value => { promise = value; } }); await promise; },
    request: (path, options = {}) => {
      let result;
      listeners.fetch({ request: { url: new URL(path, origin).href, method: 'GET', mode: 'cors', ...options },
        respondWith: value => { result = value; } });
      return result;
    },
  };
}
for (const scope of ['/', '/feedarr/']) {
  test(`install and cache rotation preserve unrelated caches at ${scope}`, async () => {
    const h = harness(scope); await h.lifecycle('install'); await h.lifecycle('activate');
    assert(h.stores.has('feedarr-pwa-v5')); assert(!h.stores.has('feedarr-pwa-v4')); assert(h.stores.has('unrelated-cache'));
    const urls = [...h.stores.get('feedarr-pwa-v5').keys()];
    for (const file of ['manifest.webmanifest', 'icon-192.png', 'icon-512.png', 'favicon.png', 'favicon-32.png', 'favicon-ios.png', 'icon-maskable-512.png', 'feedarr-logo.png'])
      assert(urls.includes(`https://feedarr.test${scope}${file}?v=${version}`), file);
  });
  test(`versioned icons remain network-first and work offline at ${scope}`, async () => {
    const h = harness(scope); await h.lifecycle('install');
    const path = `${scope}icon-192.png?v=${version}`;
    assert.equal(await (await h.request(path)).text(), 'network');
    h.offline(); assert.equal(await (await h.request(path)).text(), 'network');
  });
  test(`hashed app assets and landing-page fallback remain cached at ${scope}`, async () => {
    const h = harness(scope); await h.lifecycle('install');
    const asset = `${scope}assets/app-abc.js`;
    assert.equal(await (await h.request(asset)).text(), 'network');
    h.offline(); assert.equal(await (await h.request(asset)).text(), 'network');
    assert.equal(await (await h.request(scope, { mode: 'navigate' })).text(), 'precache');
  });
  test(`API, writes and cross-origin requests are untouched at ${scope}`, () => {
    const h = harness(scope);
    assert.equal(h.request(`${scope}api/posters/1`), undefined);
    assert.equal(h.request(`${scope}icon-192.png?v=${version}`, { method: 'POST' }), undefined);
    assert.equal(h.request('https://other.test/icon-192.png'), undefined);
  });
}
