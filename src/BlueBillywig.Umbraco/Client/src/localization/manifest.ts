/*
 * Backoffice translations. Each culture is a `localization` extension whose file default-exports a dictionary; Umbraco loads the
 * file for the user's UI culture and, for a key the file lacks, falls back to the `en` set (this package's `en` is merged into
 * Umbraco's own `en`, so a missing key shows English, never the key). The keys live in the area `bluebillywig`, read as
 * `bluebillywig_<name>` (`this.localize.term("bluebillywig_save")`, `<umb-localize key="bluebillywig_save">`, or `#bluebillywig_save`
 * in a manifest label).
 *
 * The visitor-facing texts (consent placeholder and so on) are not here: they are rendered on the server, see
 * `Localization/VisitorText.cs`.
 */
const cultures: Array<{ culture: string; name: string; js: () => Promise<{ default: UmbLocalizationDictionaryLike }> }> = [
  { culture: "en", name: "English", js: () => import("./en.js") },
  { culture: "da", name: "Danish", js: () => import("./da.js") },
  { culture: "de", name: "German", js: () => import("./de.js") },
  { culture: "fi", name: "Finnish", js: () => import("./fi.js") },
  { culture: "fr", name: "French", js: () => import("./fr.js") },
  { culture: "nb", name: "Norwegian Bokmål", js: () => import("./nb.js") },
  { culture: "nl", name: "Dutch", js: () => import("./nl.js") },
  { culture: "sv", name: "Swedish", js: () => import("./sv.js") },
];

type UmbLocalizationDictionaryLike = Record<string, Record<string, string>>;

export const manifests: Array<UmbExtensionManifest> = cultures.map((c) => ({
  type: "localization",
  alias: `BlueBillywig.Localization.${c.culture}`,
  name: `Blue Billywig ${c.name} backoffice localization`,
  meta: { culture: c.culture },
  js: c.js,
}));
