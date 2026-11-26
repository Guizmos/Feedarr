import test from "node:test";
import assert from "node:assert/strict";
import { directRssHttpError } from "./directRssErrors.js";

for (const status of [400, 401, 403, 404, 405, 500, 502]) {
  test(`RSS HTTP ${status} remains useful without exposing response secrets`, () => {
    const message = directRssHttpError({ status, message: "https://tracker.test/rss?passkey=DO-NOT-SHOW", payload: { detail: "DO-NOT-SHOW", title: "DO-NOT-SHOW" } });
    assert.match(message, new RegExp(`HTTP ${status}`));
    assert.doesNotMatch(message, /DO-NOT-SHOW|tracker\.test|passkey/);
  });
}
test("RSS validation identifies a mandatory category and normalized API errors", () => {
  assert.match(directRssHttpError({ status: 400, payload: { errors: { "Feeds[0].CategoryKey": ["Untrusted value"] } } }), /Catégorie Feedarr obligatoire/);
  assert.match(directRssHttpError({ status: 400, payload: { title: "Unknown RSS feed" } }), /Identifiant de flux RSS inconnu/);
  assert.match(directRssHttpError({ status: 400, payload: { error: "RSS URL or Feedarr category invalid" } }), /URL RSS ou catégorie/);
  assert.equal(typeof directRssHttpError({ status: 400, payload: { error: "__proto__" } }), "string");
});
test("RSS errors distinguish Feedarr authentication, outdated routes and network", () => {
  assert.match(directRssHttpError({ status: 401 }), /Session Feedarr/);
  assert.match(directRssHttpError({ status: 404 }), /Recompile puis redémarre/);
  assert.match(directRssHttpError({ status: 405 }), /backend Feedarr est à jour/);
  assert.match(directRssHttpError(new Error("secret")), /injoignable ou délai dépassé/);
  assert.match(directRssHttpError({ code: "ssrf_blocked", status: 403 }), /protection SSRF/);
});
