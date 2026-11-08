import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// Execute the real JSX handlers and effects. Only React's rendering boundary and
// visual-only children are replaced; API parsing, storage and workflow code run.
const bundles = new Map();
async function componentBundle(name) {
  if (bundles.has(name)) return bundles.get(name);
  const result = await build({
    entryPoints: [fileURLToPath(new URL(/\.jsx?$/.test(name) ? name : `../${name}.jsx`, import.meta.url))],
    bundle: true, write: false, format: "cjs", platform: "node", logLevel: "silent",
    plugins: [{ name: "render-boundary", setup(builder) {
      builder.onResolve({ filter: /^react$/ }, () => ({ path: "test-react", external: true }));
      builder.onResolve({ filter: /useSubbar\.js$/ }, () => ({ path: "test-layout", external: true }));
      builder.onResolve({ filter: /\/(?:ui\/[^/]+|CategoryMappingBoard|IndexersSyncSettingsCard)\.jsx$/ }, (args) => ({ path: args.path, namespace: "visual" }));
      builder.onLoad({ filter: /.*/, namespace: "visual" }, (args) => ({ contents: `export default ${JSON.stringify(args.path)}; export const CategoryBubble = "CategoryBubble";` }));
    } }],
  });
  bundles.set(name, result.outputFiles[0].text);
  return result.outputFiles[0].text;
}

async function mount(name, fetcher, storage = new Map(), props = {}) {
  let cursor = 0;
  const slots = [];
  let effects = [];
  const localStorage = {
    getItem: (key) => storage.get(key) ?? null,
    setItem: (key, value) => storage.set(key, String(value)),
    removeItem: (key) => storage.delete(key),
  };
  const react = {
    createElement: (type, properties, ...children) => ({ type, props: { ...properties, children } }),
    useState(initial) {
      const index = cursor++;
      if (!(index in slots)) slots[index] = typeof initial === "function" ? initial() : initial;
      return [slots[index], (value) => { slots[index] = typeof value === "function" ? value(slots[index]) : value; }];
    },
    useRef(initial) { const [ref] = react.useState(() => ({ current: initial })); return ref; },
    useMemo(fn, dependencies) {
      const index = cursor++;
      const cached = slots[index];
      if (!cached || !dependencies || dependencies.some((item, i) => item !== cached.dependencies[i])) {
        slots[index] = { dependencies, value: fn() };
      }
      return slots[index].value;
    },
    useCallback(fn, dependencies) { return react.useMemo(() => fn, dependencies); },
    useEffect(fn, dependencies) {
      const index = cursor++;
      if (!slots[index] || !dependencies || dependencies.some((item, i) => item !== slots[index][i])) {
        slots[index] = dependencies;
        effects.push(fn);
      }
    },
  };
  let subbar;
  const setSubbar = (content) => { subbar = content; };
  const module = { exports: {} };
  const execute = new Function("require", "module", "exports", "window", "localStorage", "fetch", "setTimeout", "clearTimeout", "console", await componentBundle(name));
  execute((id) => id === "test-react" ? react : { useSubbarSetter: () => setSubbar }, module, module.exports, { localStorage, dispatchEvent() {} }, localStorage, fetcher, (fn) => { fn(); return 1; }, () => {}, { log() {}, info() {}, error() {} });
  let tree;
  function render() {
    cursor = 0;
    tree = module.exports.default(props);
    const pending = effects;
    effects = [];
    pending.forEach((fn) => fn());
    return tree;
  }
  async function settle() { for (let i = 0; i < 35; i++) { render(); await Promise.resolve(); } return render(); }
  await settle();
  return { render, settle, get tree() { return tree; }, get subbar() { return subbar; }, storage };
}
function nodes(tree, predicate) {
  if (!tree || typeof tree !== "object") return [];
  if (Array.isArray(tree)) return tree.flatMap((child) => nodes(child, predicate));
  return [...(predicate(tree) ? [tree] : []), ...nodes(tree.props?.children, predicate)];
}
function text(tree) {
  if (typeof tree === "string") return tree;
  if (Array.isArray(tree)) return tree.map(text).join(" ");
  return tree?.props ? text(tree.props.children) : "";
}
const json = (payload, status = 200) => new Response(JSON.stringify(payload), {
  status, headers: { "content-type": "application/problem+json" },
});
async function configureProvider(view) {
  nodes(view.tree, (node) => node.type === "select")[0].props.onChange({ target: { value: "prowlarr" } });
  await view.settle();
  nodes(view.tree, (node) => node.type === "input")[0].props.onChange({ target: { value: "http://prowlarr:9696" } });
  nodes(view.tree, (node) => node.type === "input")[1].props.onChange({ target: { value: "test-key" } });
  await view.settle();
}
function primary(view) { return nodes(view.tree, (node) => node.type === "button" && node.props.className?.includes("btn-accent")).at(-1); }

test("provider SSRF refusal never retries, saves or offers manual fallback", async () => {
  const calls = [];
  const statuses = [];
  const view = await mount("Step3JackettConn", async (path) => {
    calls.push(path);
    return json({ code: "ssrf_blocked", title: "unexpected token <" }, 403);
  }, new Map(), { onStatusChange: (status) => statuses.push(status) });
  await configureProvider(view);
  await primary(view).props.onClick();
  await view.settle();
  assert.equal(calls.length, 1);
  assert.match(text(view.tree), /Connexion bloquée.*TrustedHosts/);
  assert.doesNotMatch(text(view.tree), /Sauvegarder \(manuel\)/);
  assert.equal(statuses.at(-1).ready, false);
  assert.equal(view.storage.get("feedarr:prowlarrManualOnly"), "false");
  assert.equal(view.storage.get("feedarr:prowlarrConfigured"), undefined);
});

test("ordinary discovery failure keeps manual saving and wizard resume", async () => {
  const storage = new Map();
  const statuses = [];
  const view = await mount("Step3JackettConn", async (path) => path.startsWith("/api/setup/")
    ? json({ id: 1 }) : json({ title: "provider discovery unavailable" }, 502), storage);
  await configureProvider(view);
  await primary(view).props.onClick();
  await view.settle();
  assert.match(text(primary(view)), /Sauvegarder \(manuel\)/);
  await primary(view).props.onClick();
  await view.settle();
  assert.equal(storage.get("feedarr:prowlarrConfigured"), "true");
  assert.equal(storage.get("feedarr:prowlarrManualOnly"), "true");
  await mount("Step3JackettConn", async () => json({}), storage, { onStatusChange: (status) => statuses.push(status) });
  assert.equal(statuses.at(-1).ready, true);
  assert.equal(statuses.at(-1).manualOnly, true);
});

test("indexer step blocks cached manual fallback after a fresh SSRF discovery refusal", async () => {
  const storage = new Map([
    ["feedarr:prowlarrBaseUrl", "http://prowlarr:9696"],
    ["feedarr:prowlarrConfigured", "true"], ["feedarr:prowlarrManualOnly", "true"],
  ]);
  const view = await mount("Step31JackettIndexers", async (path) => {
    if (path === "/api/providers") return json([{ id: 1, type: "prowlarr", baseUrl: "http://prowlarr:9696", hasApiKey: true, enabled: true }]);
    if (path.endsWith("/indexers")) return json({ code: "ssrf_blocked" }, 403);
    return json([]);
  }, storage);
  assert.match(text(view.tree), /Connexion bloquée/);
  const select = nodes(view.tree, (node) => node.type === "select")[0];
  assert.equal(select.props.disabled, true);
  assert.equal(storage.get("feedarr:prowlarrManualOnly"), "false");
});

test("a source saved before SSRF failure is refreshed without claiming CAPS or sync success", async () => {
  let created = false;
  const calls = [];
  const source = { id: 7, name: "Private indexer", torznabUrl: "http://prowlarr:9696/1/api", enabled: true };
  const view = await mount("Step31JackettIndexers", async (path, options) => {
    calls.push({ path, method: options.method || "GET", body: options.body ? JSON.parse(options.body) : null });
    if (path === "/api/providers") return json([{ id: 1, type: "prowlarr", baseUrl: "http://prowlarr:9696", enabled: true, hasApiKey: true }]);
    if (path.endsWith("/indexers")) return json([{ id: 1, name: source.name, torznabUrl: source.torznabUrl }]);
    if (path.includes("/caps")) return json({ categories: [{ id: 2000, name: "Movies" }], warnings: [] });
    if (path === "/api/sources" && options.method === "POST") {
      created = true;
      return json({ code: "ssrf_blocked" }, 403);
    }
    return json(created ? [source] : []);
  });
  nodes(view.tree, (node) => node.type === "select")[0].props.onChange({ target: { value: "1" } });
  await view.settle();
  assert.match(text(view.tree), /Categories chargees/);
  await primary(view).props.onClick();
  await view.settle();
  assert.match(text(view.tree), /source peut déjà être enregistrée/);
  assert.doesNotMatch(text(view.tree), /Categories chargees|Indexeur ajoute/);
  assert.equal(calls.filter((call) => call.method === "POST" && call.path === "/api/sources").length, 1);
  assert.equal(calls.at(-1).path, "/api/sources");
  assert.equal(calls.at(-1).method, "GET");
});

for (const inputUrl of ["http://10.50.0.15:5055", "10.50.0.15:5055"]) {
  test(`application SSRF recovery keeps selection and error for ${inputUrl}`, async () => {
    let saved = false;
    const calls = [];
    const app = { id: 2, type: "seer", baseUrl: "http://10.50.0.15:5055", hasApiKey: true };
    const view = await mount("Step4ArrApps", async (path, options) => {
      calls.push({ path, method: options.method || "GET", body: options.body ? JSON.parse(options.body) : null });
      if (path === "/api/apps" && options.method === "POST") {
        saved = true;
        return json({ code: "ssrf_blocked" }, 403);
      }
      if (path.includes("/test")) return json({ ok: true });
      return json(saved ? [app] : []);
    });
    nodes(view.tree, (node) => node.type === "select")[0].props.onChange({ target: { value: "seer" } });
    await view.settle();
    const inputs = nodes(view.tree, (node) => node.type === "input");
    inputs[1].props.onChange({ target: { value: inputUrl } });
    inputs[2].props.onChange({ target: { value: "test-key" } });
    await view.settle();
    await primary(view).props.onClick();
    await view.settle();
    assert.match(text(view.tree), /Connexion OK/);
    await primary(view).props.onClick();
    await view.settle();
    assert.match(text(view.tree), /application peut déjà être enregistrée/);
    assert.doesNotMatch(text(view.tree), /Connexion OK/);
    assert.match(text(primary(view)), /Tester/);
    const modal = nodes(view.tree, (node) => node.props?.title === "Editer: seer")[0];
    assert.equal(modal.props.open, true);
    assert.equal(nodes(view.tree, (node) => node.type === "input")[1].props.value, inputUrl);
    const creation = calls.find((call) => call.path === "/api/apps" && call.method === "POST");
    assert.equal(creation.body.baseUrl, inputUrl);
    assert.equal(creation.body.type, "seer");
    assert.equal(calls.filter((call) => call.path === "/api/apps" && call.method === "POST").length, 1);
    // Retest the existing entry; saving must now update its ID, never POST a duplicate.
    await primary(view).props.onClick();
    await view.settle();
    await primary(view).props.onClick();
    await view.settle();
    assert.ok(calls.some((call) => call.path === "/api/apps/2" && call.method === "PUT"));
    assert.equal(calls.filter((call) => call.path === "/api/apps" && call.method === "POST").length, 1);
  });
}

for (const inputUrl of ["10.50.0.15:5055", "http://10.50.0.15:5055"]) {
  test(`application settings recover the saved ID after SSRF for ${inputUrl}`, async () => {
    let saved = false;
    const calls = [];
    const app = { id: 12, type: "seer", baseUrl: "http://10.50.0.15:5055", hasApiKey: true };
    const view = await mount("../../../pages/settings/hooks/useArrApplications.js", async (path, options) => {
      calls.push({ path, method: options.method || "GET", body: options.body ? JSON.parse(options.body) : null });
      if (path === "/api/apps" && options.method === "POST") {
        saved = true;
        return json({ code: "ssrf_blocked" }, 403);
      }
      if (path.includes("/test")) return json({ ok: true });
      return json(saved ? [app] : []);
    });
    view.tree.openArrModalAdd();
    await view.settle();
    view.tree.setArrModalType("seer");
    view.tree.setArrModalBaseUrl(inputUrl);
    view.tree.setArrModalApiKey("test-key");
    await view.settle();
    await view.tree.testArrModal();
    await view.settle();
    assert.equal(view.tree.arrModalTested, true);
    await view.tree.saveArrModal();
    await view.settle();
    assert.equal(view.tree.arrModalMode, "edit");
    assert.equal(view.tree.arrModalApp.id, 12);
    assert.equal(view.tree.arrModalType, "seer");
    assert.equal(view.tree.arrModalBaseUrl, inputUrl);
    assert.equal(view.tree.arrModalTested, false);
    assert.equal(view.tree.arrModalOpen, true);
    assert.match(view.tree.arrModalError, /Connexion bloquée.*application peut déjà être enregistrée/);
    assert.equal(calls.find((call) => call.method === "POST" && call.path === "/api/apps").body.baseUrl, inputUrl);
    await view.tree.testArrModal();
    await view.settle();
    await view.tree.saveArrModal();
    await view.settle();
    assert.ok(calls.some((call) => call.method === "PUT" && call.path === "/api/apps/12"));
    assert.equal(calls.filter((call) => call.method === "POST" && call.path === "/api/apps").length, 1);
  });
}

test("application settings retain the edited ID when another record matches the new URL", async () => {
  const app = { id: 12, type: "seer", baseUrl: "http://old-seer:5055", hasApiKey: true };
  const other = { id: 13, type: "seer", baseUrl: "http://10.50.0.15:5055", hasApiKey: true };
  const view = await mount("../../../pages/settings/hooks/useArrApplications.js", async (path, options) => {
    if (options.method === "PUT") return json({ code: "ssrf_blocked" }, 403);
    if (path === "/api/apps") return json([other, app]);
    return json({});
  });
  view.tree.openArrModalEdit(app);
  await view.settle();
  view.tree.setArrModalBaseUrl("10.50.0.15:5055");
  await view.settle();
  await view.tree.saveArrModal();
  await view.settle();
  assert.equal(view.tree.arrModalApp.id, 12);
  assert.equal(view.tree.arrModalType, "seer");
  assert.equal(view.tree.arrModalBaseUrl, "10.50.0.15:5055");
  assert.match(view.tree.arrModalError, /Connexion bloquée/);
});

for (const inputUrl of ["prowlarr:9696/1/api", "http://prowlarr:9696/1/api"]) {
  test(`source settings recover a partially saved source for ${inputUrl}`, async () => {
    let saved = false;
    const calls = [];
    const source = { id: 7, name: "Private source", torznabUrl: "http://prowlarr:9696/1/api", enabled: true };
    const view = await mount("../../../pages/Indexers.jsx", async (path, options) => {
      calls.push({ path, method: options.method || "GET", body: options.body ? JSON.parse(options.body) : null });
      if (path === "/api/providers") return json([{ id: 1, type: "prowlarr", baseUrl: "http://prowlarr:9696", enabled: true }]);
      if (path === "/api/sources" && options.method === "POST") {
        saved = true;
        return json({ code: "ssrf_blocked" }, 403);
      }
      if (path === "/api/sources") return json(saved ? [source] : []);
      if (path.includes("/caps")) return json({ categories: [{ id: 2000, name: "Movies" }], warnings: [] });
      return json([]);
    });
    nodes(view.subbar, (node) => node.props?.label === "Ajouter")[0].props.onClick();
    await view.settle();
    nodes(view.tree, (node) => node.type === "select")[0].props.onChange({ target: { value: "1" } });
    await view.settle();
    nodes(view.tree, (node) => node.type === "select")[1].props.onChange({ target: { value: "__manual__" } });
    await view.settle();
    nodes(view.tree, (node) => node.type === "input" && node.props.placeholder === "Colle l'URL Copy Torznab Feed")[0]
      .props.onChange({ target: { value: inputUrl } });
    nodes(view.tree, (node) => node.type === "input" && node.props.placeholder === "Nom de l'indexeur")[0]
      .props.onChange({ target: { value: source.name } });
    await view.settle();
    const button = (label) => nodes(view.tree, (node) => node.type === "button" && text(node).trim() === label).at(-1);
    await button("Tester").props.onClick();
    await view.settle();
    button("Suivant").props.onClick();
    await view.settle();
    await button("Enregistrer").props.onClick();
    await view.settle();
    assert.match(text(view.tree), /Connexion bloquée.*source peut déjà être enregistrée/);
    const creation = calls.find((call) => call.method === "POST" && call.path === "/api/sources");
    assert.equal(creation.body.torznabUrl, inputUrl);
    assert.equal(creation.body.providerId, 1);
    await button("Enregistrer").props.onClick();
    await view.settle();
    assert.ok(calls.some((call) => call.method === "PUT" && call.path === "/api/sources/7"));
    assert.equal(calls.filter((call) => call.method === "POST" && call.path === "/api/sources").length, 1);
  });
}
