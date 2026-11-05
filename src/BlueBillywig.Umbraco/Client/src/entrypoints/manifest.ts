export const manifests: Array<UmbExtensionManifest> = [
  {
    name: "Blue Billywig Entrypoint",
    alias: "BlueBillywig.Entrypoint",
    type: "backofficeEntryPoint",
    js: () => import("./entrypoint.js"),
  },
];
