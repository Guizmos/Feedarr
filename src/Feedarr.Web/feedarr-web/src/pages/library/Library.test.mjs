import test from "node:test";
import assert from "node:assert/strict";
import { act } from "react";
import { fixture } from "../../test/authDomFixture.mjs";
const json = value => Promise.resolve(Response.json(value));
const nav = () => document.querySelector('nav[aria-label="Pagination de la bibliothèque"]');
async function select(label, value) {
  const input = document.querySelector(`select[aria-label="${label}"]`); assert.ok(input, label);
  await act(async () => { input.value = value; input.dispatchEvent(new window.Event("change", { bubbles: true })); });
}
test("Library loads beyond 200, displays the SQL total, reaches the end and resets filters", async () => {
  const calls = [];
  let holdNext = false;
  let resolveOldPage;
  const f = await fixture(url => {
    const parsed = new URL(url, "https://fixture.test"), path = parsed.pathname, p = parsed.searchParams;
    if (path === "/api/auth/session") return json({ authenticated: true, authRequired: true, authConfigured: true, csrfToken: "fixture", initialAdminRequired: false });
    if (path === "/api/settings/ui") return json({ theme: "light", defaultView: "list", defaultLimit: 100, uiLanguage: "fr-FR" });
    if (path === "/api/sources") return json([{ id: 1, name: "alpha", enabled: 1 }, { id: 2, name: "zulu", enabled: 1 }]);
    if (path === "/api/apps") return json([]);
    if (path === "/api/feed/releases") {
      calls.push(new URLSearchParams(p));
      const totalCount = p.get("quality") ? 25 : 305;
      const start = Number(p.get("cursor") || 0), end = Math.min(totalCount, start + Number(p.get("limit")));
      const response = { totalCount, items: Array.from({ length: end - start }, (_, i) => ({ id: start + i + 1, sourceId: i % 2 + 1, title: "Amélie", unifiedCategoryKey: "films", resolution: "1080p", publishedAt: 100 })), pagination: { hasMore: end < totalCount, nextCursor: String(end) }, facets: start ? null : { categories: ["films", "series"], qualities: ["1080p", "720p"] } };
      if (holdNext && start > 0) {
        holdNext = false;
        return new Promise(resolve => { resolveOldPage = () => resolve(Response.json(response)); });
      }
      return json(response);
    }
    assert.ok(!/^\/api\/feed\/\d/.test(path), "no legacy per-source window");
    return json({});
  }, "/library?q=Amelie");
  try {
    await f.waitFor(() => nav() && document.body.textContent.includes("Résultats : 305") && calls.at(-1)?.get("q") === "Amelie");
    assert.equal(calls.at(-1).get("cursor"), null);
    await f.click("Suivant"); await f.waitFor(() => nav()?.textContent.includes("Page 2"));
    assert.equal(calls.at(-1).get("cursor"), "100");
    assert.ok(document.body.textContent.includes("Résultats : 305"));
    await f.click("Précédent"); await f.waitFor(() => nav()?.textContent.includes("Page 1"));
    assert.equal(calls.at(-1).get("cursor"), null);
    await select("Limit", "all");
    await f.waitFor(() => nav()?.textContent.includes("100 releases affichées sur 305") && nav()?.textContent.includes("Charger plus"));
    assert.equal(calls.at(-1).get("limit"), "100");
    await f.click("Charger plus");
    await f.waitFor(() => nav()?.textContent.includes("200 releases affichées sur 305"));
    await f.click("Charger plus");
    await f.waitFor(() => nav()?.textContent.includes("300 releases affichées sur 305"));
    await f.click("Charger plus");
    await f.waitFor(() => nav()?.textContent.includes("305 releases affichées sur 305"));
    assert.ok(!nav().textContent.includes("Charger plus"));
    assert.ok(document.body.textContent.includes("Résultats : 305"));
    await f.click("Filtre"); await select("Qualité", "720p");
    await f.waitFor(() => calls.at(-1)?.get("quality") === "720p" && nav());
    assert.equal(calls.at(-1).get("cursor"), null);
    await f.waitFor(() => document.body.textContent.includes("Résultats : 25"));
    assert.ok(nav().textContent.includes("25 releases affichées sur 25"));
    assert.ok([...document.querySelector('select[aria-label="Qualité"]').options].some(o => o.value === "1080p"));
    await act(async () => document.querySelector("button.library-sort").click());
    await f.waitFor(() => calls.at(-1)?.get("sort") === "title" && nav());
    assert.equal(calls.at(-1).get("cursor"), null);

    // A late append from the previous query must not leak into a changed filter.
    await select("Qualité", "");
    await f.waitFor(() => nav()?.textContent.includes("100 releases affichées sur 305"));
    holdNext = true;
    await f.click("Charger plus");
    await f.waitFor(() => resolveOldPage);
    await select("Qualité", "1080p");
    await f.waitFor(() => nav()?.textContent.includes("25 releases affichées sur 25"));
    await act(async () => resolveOldPage());
    assert.ok(nav().textContent.includes("25 releases affichées sur 25"));
    assert.ok(document.body.textContent.includes("Résultats : 25"));
  } finally { await f.close(); }
});
