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
test("Library paginates without accumulating, resets filters and keeps Unicode results", async () => {
  const calls = [];
  const f = await fixture(url => {
    const parsed = new URL(url, "https://fixture.test"), path = parsed.pathname, p = parsed.searchParams;
    if (path === "/api/auth/session") return json({ authenticated: true, authRequired: true, authConfigured: true, csrfToken: "fixture", initialAdminRequired: false });
    if (path === "/api/settings/ui") return json({ theme: "light", defaultView: "list", defaultLimit: 100, uiLanguage: "fr-FR" });
    if (path === "/api/sources") return json([{ id: 1, name: "alpha", enabled: 1 }, { id: 2, name: "zulu", enabled: 1 }]);
    if (path === "/api/apps") return json([]);
    if (path === "/api/feed/releases") {
      calls.push(new URLSearchParams(p));
      const start = Number(p.get("cursor") || 0), end = Math.min(1205, start + Number(p.get("limit")));
      return json({ items: Array.from({ length: end - start }, (_, i) => ({ id: start + i + 1, sourceId: i % 2 + 1, title: "Amélie", unifiedCategoryKey: "films", resolution: "1080p", publishedAt: 100 })), pagination: { hasMore: end < 1205, nextCursor: String(end) }, facets: start ? null : { categories: ["films", "series"], qualities: ["1080p", "720p"] } });
    }
    assert.ok(!/^\/api\/feed\/\d/.test(path), "no legacy per-source window");
    return json({});
  }, "/library?q=Amelie");
  try {
    await f.waitFor(() => nav() && document.body.textContent.includes("Résultats: 100") && calls.at(-1)?.get("q") === "Amelie");
    assert.equal(calls.at(-1).get("cursor"), null);
    await f.click("Suivant"); await f.waitFor(() => nav()?.textContent.includes("Page 2"));
    assert.equal(calls.at(-1).get("cursor"), "100");
    assert.ok(document.body.textContent.includes("Résultats: 100"));
    await f.click("Précédent"); await f.waitFor(() => nav()?.textContent.includes("Page 1"));
    assert.equal(calls.at(-1).get("cursor"), null);
    await select("Limit", "all"); await f.waitFor(() => document.body.textContent.includes("Résultats: 200"));
    assert.equal(calls.at(-1).get("limit"), "200");
    await f.click("Filtre"); await select("Qualité", "720p");
    await f.waitFor(() => calls.at(-1)?.get("quality") === "720p" && nav());
    assert.equal(calls.at(-1).get("cursor"), null);
    assert.ok([...document.querySelector('select[aria-label="Qualité"]').options].some(o => o.value === "1080p"));
  } finally { await f.close(); }
});
