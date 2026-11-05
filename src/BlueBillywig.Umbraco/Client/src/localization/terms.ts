import type { UmbLocalizationDictionary } from "@umbraco-cms/backoffice/localization-api";
import en from "./en.js";

/**
 * A translate function with the shape of `this.localize.term`: a key plus optional arguments. Pure helpers (error texts, limits
 * text) take one, so they can be tested in Node and so an element passes `(k, ...a) => this.localize.term(k, ...a)`.
 */
export type Term = (key: string, ...args: unknown[]) => string;

/** The key of a term: the dictionaries nest one level (`bluebillywig.save`), Umbraco flattens that to `bluebillywig_save`. */
export function flatten(dictionary: UmbLocalizationDictionary): Record<string, string> {
  const flat: Record<string, string> = {};
  for (const [area, terms] of Object.entries(dictionary)) {
    for (const [name, value] of Object.entries(terms)) {
      flat[`${area}_${name}`] = typeof value === "string" ? value : "";
    }
  }
  return flat;
}

/** Umbraco's interpolation: `{0}` and `%0%` are replaced by the argument with that index; a missing argument leaves the marker. */
export function interpolate(term: string, args: unknown[]): string {
  if (args.length === 0) return term;
  return term.replace(/(%(\d+)%|\{(\d+)\})/g, (match, _all, a, b) => {
    const value = args[Number(a ?? b)];
    return value === undefined ? match : String(value);
  });
}

const english = flatten(en);

/**
 * The English source text, with the same contract as Umbraco's `term`: an unknown key comes back as the key. It is the default
 * of every pure helper, so they work (and are tested) without a rendered element.
 */
export const englishTerm: Term = (key, ...args) => (key in english ? interpolate(english[key], args) : key);

/**
 * The terms for one culture on top of the English ones. This is what Umbraco does at runtime (culture, then language, then the
 * `en` set); it exists here so a test can show that a culture without a key still reads English.
 */
export function termFor(culture: Record<string, string>): Term {
  return (key, ...args) => {
    const value = culture[key];
    if (typeof value === "string" && value.length > 0) return interpolate(value, args);
    return englishTerm(key, ...args);
  };
}
