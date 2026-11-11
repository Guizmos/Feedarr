import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
import path from "node:path";

const srcDir = fileURLToPath(new URL("./src", import.meta.url));

export default defineConfig({
  define: { __FEEDARR_BUILD__: JSON.stringify(Date.now().toString(36)) },
  plugins: [react(), {
    name: "feedarr-public-pwa-assets",
    generateBundle(_options, bundle) {
      const assets = Object.keys(bundle).filter((name) => /^assets\/.*\.(js|css|woff2?|png|svg)$/.test(name));
      this.emitFile({ type: "asset", fileName: "pwa-assets.json", source: JSON.stringify(assets) });
    },
  }],
  build: {
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes("node_modules")) return;
          if (id.includes("lucide-react")) return "vendor-icons";
          if (id.includes("react-router-dom")) return "vendor-router";
          if (
            id.includes("/react/") ||
            id.includes("\\react\\") ||
            id.includes("/react-dom/") ||
            id.includes("\\react-dom\\") ||
            id.includes("/scheduler/") ||
            id.includes("\\scheduler\\")
          ) {
            return "vendor-react";
          }
          return;
        },
      },
    },
  },
  resolve: {
    alias: {
      "@": srcDir,
      "@api": path.resolve(srcDir, "api"),
      "@utils": path.resolve(srcDir, "utils"),
      "@pages": path.resolve(srcDir, "pages"),
      "@ui": path.resolve(srcDir, "ui"),
      "@hooks": path.resolve(srcDir, "hooks"),
      "@layout": path.resolve(srcDir, "layout"),
    },
  },
  server: {
    proxy: {
      "/api": {
        target: "http://localhost:5003",
        changeOrigin: true,
      },
    },
  },
});
