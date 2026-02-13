import test from "node:test";
import assert from "node:assert/strict";
import {
  buildProviderRows,
  maskProviderUrl,
  normalizeProviderBaseUrl,
  labelForProviderType,
} from "./providersListModel.js";

test("buildProviderRows keeps Jackett and Prowlarr rows when enabled", () => {
  const rows = buildProviderRows([
    {
      id: 2,
      type: "prowlarr",
      name: "Prowlarr",
      baseUrl: "http://localhost:9696",
      enabled: true,
      lastTestOkAt: 1739200000,
    },
    {
      id: 1,
      type: "jackett",
      name: "Jackett",
      baseUrl: "http://localhost:9117",
      enabled: true,
      lastTestOkAt: 1739200000,
    },
  ]);

  assert.equal(rows.length, 2);
  assert.equal(rows[0].id, 1);
  assert.equal(rows[1].id, 2);
  assert.equal(rows[0]._typeLabel, "Jackett");
  assert.equal(rows[1]._typeLabel, "Prowlarr");
});

test("provider helpers normalize labels and urls safely", () => {
  assert.equal(labelForProviderType("prowlarr"), "Prowlarr");
  assert.equal(labelForProviderType("unknown"), "Jackett");

  assert.equal(normalizeProviderBaseUrl(" http://localhost:9117/// "), "http://localhost:9117");

  assert.equal(maskProviderUrl("http://localhost:9117/api/v2.0"), "http://localhost:9117/•••");
  assert.equal(maskProviderUrl("short"), "•••");
  assert.equal(maskProviderUrl("not-a-url-long-enough"), "not-a-ur•••");
});
