import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  plugins: [svelte()],
  base: "/demo/",
  build: {
    outDir: "../wwwroot/demo",
    emptyOutDir: true,
  },
  server: {
    host: "127.0.0.1",
    proxy: {
      "/api": {
        target: "https://localhost:7092",
        // Trust the local ASP.NET development certificate only in the dev proxy.
        secure: false,
      },
    },
  },
});
