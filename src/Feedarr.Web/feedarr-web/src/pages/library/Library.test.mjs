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
    await f.waitFor(() => document.body.textContent.includes("Résultats : 305"));
    assert.equal(nav(), null);
    await select("Limit", "200");
    await f.waitFor(() => calls.at(-1)?.get("limit") === "200");
    assert.equal(nav(), null);
    await select("Limit", "500");
    await f.waitFor(() => calls.at(-1)?.get("cursor") === "200");
    assert.equal(nav(), null);
    await select("Limit", "all");
    await f.waitFor(() => nav()?.textContent.includes("Page 1 sur 4"));
    assert.ok(nav().querySelector("button").disabled);
    await f.click("Suivant");
    await f.waitFor(() => nav()?.textContent.includes("Page 2 sur 4"));
    assert.equal(calls.at(-1).get("cursor"), "100");
    const count = calls.length;
    await f.click("Précédent");
    await f.waitFor(() => nav()?.textContent.includes("Page 1 sur 4"));
    assert.equal(calls.length, count);
    await f.click("Suivant");
    await f.waitFor(() => nav()?.textContent.includes("Page 2 sur 4"));
    assert.equal(calls.length, count);
    await f.click("Suivant");
    await f.waitFor(() => nav()?.textContent.includes("Page 3 sur 4"));
    await f.click("Suivant");
    await f.waitFor(() => nav()?.textContent.includes("Page 4 sur 4"));
    assert.ok(nav().querySelectorAll("button")[1].disabled);
    assert.equal(nav().style.justifyContent, "center");
    await f.click("Filtre"); await select("Qualité", "720p");
    await f.waitFor(() => document.body.textContent.includes("Résultats : 25") && nav()?.textContent.includes("Page 1 sur 1"));
    assert.equal(calls.at(-1).get("cursor"), null);
    await act(async () => document.querySelector("button.library-sort").click());
    await f.waitFor(() => calls.at(-1)?.get("sort") === "title" && nav());
    assert.equal(calls.at(-1).get("cursor"), null);
    await select("Qualité", "");
    await f.waitFor(() => nav()?.textContent.includes("Page 1 sur 4"));
    holdNext = true;
    await f.click("Suivant");
    await f.waitFor(() => resolveOldPage);
    await select("Qualité", "1080p");
    await f.waitFor(() => document.body.textContent.includes("Résultats : 25") && nav()?.textContent.includes("Page 1 sur 1"));
    await act(async () => resolveOldPage());
    assert.ok(nav().textContent.includes("Page 1 sur 1"));
  } finally { await f.close(); }
});
