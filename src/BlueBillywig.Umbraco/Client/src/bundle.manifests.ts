import { manifests as entrypoints } from "./entrypoints/manifest.js";
import { manifests as dashboards } from "./dashboards/manifest.js";
import { manifests as embed } from "./embed/manifest.js";
import { manifests as library } from "./library/manifest.js";
import { manifests as rte } from "./rte/manifest.js";
import { manifests as localization } from "./localization/manifest.js";

// Collates the manifests of every part of the package. Loaded through umbraco-package.json.
export const manifests: Array<UmbExtensionManifest> = [
  ...entrypoints,
  ...dashboards,
  ...embed,
  ...library,
  ...rte,
  ...localization,
];
