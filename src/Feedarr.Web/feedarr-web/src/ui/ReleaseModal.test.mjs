import test from "node:test";
import assert from "node:assert/strict";
import React, { act } from "react";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createDomEnvironment } from "../test/domEnvironment.mjs";
import { getTrackerPageUrl } from "../utils/trackerPageUrl.js";

const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: "export {default} from './src/ui/ReleaseModal.jsx';", resolveDir: fileURLToPath(new URL("../../", import.meta.url)), loader: "jsx" },
  jsx: "automatic", bundle: true, platform: "node", format: "esm", write: false,
  define: { "import.meta.env": JSON.stringify({ BASE_URL: "/" }) },
  plugins: [{ name: "react", setup(b) {
    b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true }));
  } }],
});
const temp = await mkdtemp(join(tmpdir(), "feedarr-release-link-"));
await writeFile(join(temp, "modal.mjs"), compiled.outputFiles[0].text);
const { default: ReleaseModal } = await import(pathToFileURL(join(temp, "modal.mjs")));
await rm(temp, { recursive: true });

for (const indexer of ["C411", "TR4KER", "G3MINI", "YggReborn"]) {
  test(`${indexer}: dedicated URL opens independently; missing URL stays a pill`, async () => {
    const env = createDomEnvironment();
    const { createRoot } = await import("react-dom/client");
    const root = createRoot(document.getElementById("root"));
    let closes = 0, downloads = 0, bubbled = 0;
    const props = { open: true, indexerLabel: indexer, indexerColor: "#567890",
      onClose: () => closes++, onDownload: () => downloads++ };
    const item = { id: 123, title: "Example", trackerPageUrl: "https://tracker.example/details/123",
      downloadUrl: "https://jackett.example/download?apikey=private", link: "magnet:?xt=urn:btih:123" };
    try {
      await act(async () => root.render(React.createElement("div", { onClick: () => bubbled++ }, React.createElement(ReleaseModal, { ...props, item }))));
      const pill = document.querySelector(".releaseModal__pillsRight a");
      assert.ok(pill);
      assert.equal(pill.href, item.trackerPageUrl);
      assert.equal(pill.target, "_blank");
      assert.equal(pill.rel, "noopener noreferrer");
      assert.equal(pill.title, "Ouvrir sur le tracker");
      assert.equal(pill.textContent, indexer);
      const className = pill.className, style = pill.getAttribute("style");
      // Cancel jsdom navigation while exercising the React click handler.
      pill.addEventListener("click", e => e.preventDefault());
      await act(async () => pill.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true })));
      assert.equal(bubbled, 0); assert.equal(closes, 0); assert.equal(downloads, 0);
      for (const trackerPageUrl of [null, "javascript:alert(1)", "https://tracker.example/release.torrent", "https://tracker.example/details/123?apikey=private"]) {
        await act(async () => root.render(React.createElement("div", { onClick: () => bubbled++ }, React.createElement(ReleaseModal, { ...props, item: { ...item, trackerPageUrl } }))));
        const fallback = document.querySelector(".releaseModal__pillsRight span");
        assert.ok(fallback);
        assert.equal(fallback.className, className);
        assert.equal(fallback.getAttribute("style"), style);
        assert.equal(document.querySelector(".releaseModal__pillsRight a"), null);
      }
    } finally { await act(async () => root.unmount()); env.restore(); }
  });
}

test("URL validation excludes credentials, downloads and search/API URLs", () => {
  for (const value of [null, "", "/details/1", "garbage", "magnet:?xt=123", "ftp://tracker.example/details/1",
    "https://user:pass@tracker.example/details/1", "https://tracker.example/a.torrent",
    "https://tracker.example/a%2Etorrent", "https://tracker.example/download/1",
    "https://jackett.example/api/v2.0/indexers/all/results/torznab/api",
    "https://prowlarr.example/12/api?t=search", "https://tracker.example/details/1?api_key=private",
    "https://tracker.example/details/1?passkey=private", "https://tracker.example/details/1?auth=private",
    "https://tracker.example/search?q=title", "https://tracker.example/%ZZ", "http://localhost/details/1"]) {
    assert.equal(getTrackerPageUrl(value), null);
  }
  assert.equal(getTrackerPageUrl(" https://tracker.example/details.php?id=42 "), "https://tracker.example/details.php?id=42");
  assert.equal(getTrackerPageUrl("http://tracker.example/details/42"), "http://tracker.example/details/42");
});
