import { defineConfig } from "vite";

export default defineConfig({
  build: {
    lib: {
      entry: "src/bundle.manifests.ts", // Bundle registers one or more manifests
      formats: ["es"],
      fileName: "blue-billywig-umbraco",
    },
    outDir: "../wwwroot/App_Plugins/BlueBillywig", // served at /App_Plugins/BlueBillywig/
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      external: [/^@umbraco/],
    },
  },
});
