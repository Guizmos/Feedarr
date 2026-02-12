import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";
import path from "node:path";

const srcDir = fileURLToPath(new URL("./src", import.meta.url));

export default defineConfig({
  plugins: [react()],
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
