import { resolve } from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  publicDir: resolve(__dirname, "../../../DryEnd.Next/src/dryend-web-client/public"),
  build: {
    outDir: resolve(__dirname, "../PaperMachine.Historian.Web/wwwroot"),
    emptyOutDir: true,
    chunkSizeWarningLimit: 600,
  },
  server: {
    port: 5188,
    proxy: {
      "/api": "http://localhost:5088",
      "/health": "http://localhost:5088",
    },
  },
});
