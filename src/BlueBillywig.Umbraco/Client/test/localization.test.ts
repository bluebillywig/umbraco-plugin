import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import en from "../src/localization/en";
import da from "../src/localization/da";
import de from "../src/localization/de";
import fi from "../src/localization/fi";
import fr from "../src/localization/fr";
import nb from "../src/localization/nb";
import nl from "../src/localization/nl";
import sv from "../src/localization/sv";
import { manifests } from "../src/localization/manifest";
import { englishTerm, flatten, interpolate, termFor } from "../src/localization/terms";
import { libraryError, nounOf, saveError } from "../src/library/errors";
import { limitsText } from "../src/library/upload-limits";

// The backoffice dictionaries (src/localization/*.ts, registered as `localization` extensions) and the visitor texts the
// server renders (src/BlueBillywig.Umbraco/Localization/Visitor/*.json, embedded in the assembly). English is the source.

const locales = { da, de, fi, fr, nb, nl, sv };
const source = flatten(en);
const placeholders = (text: string) => [...text.matchAll(/\{(\d+)\}/g)].map((m) => m[1]).sort();

describe("backoffice dictionaries", () => {
  it("English has terms, none empty", () => {
    expect(Object.keys(source).length).toBeGreaterThan(100);
    for (const [key, value] of Object.entries(source)) expect(value.trim(), key).not.toBe("");
  });

  for (const [culture, dictionary] of Object.entries(locales)) {
    describe(culture, () => {
      const terms = flatten(dictionary);

      it("has exactly the English keys", () => {
        const missing = Object.keys(source).filter((k) => !(k in terms));
        const extra = Object.keys(terms).filter((k) => !(k in source));
        expect({ missing, extra }).toEqual({ missing: [], extra: [] });
      });

      it("has no empty values", () => {
        for (const [key, value] of Object.entries(terms)) expect(value.trim(), key).not.toBe("");
      });

      it("keeps the {0} {1} placeholders of the English text", () => {
        for (const [key, value] of Object.entries(terms)) expect(placeholders(value), key).toEqual(placeholders(source[key]));
      });

      it("only has the area bluebillywig", () => {
        expect(Object.keys(dictionary)).toEqual(["bluebillywig"]);
      });
    });
  }
});

describe("localization manifests", () => {
  it("register English plus da, de, fi, fr, nb, nl and sv, once each", () => {
    const cultures = manifests.map((m) => (m as { meta: { culture: string } }).meta.culture);
    expect(cultures).toEqual(["en", "da", "de", "fi", "fr", "nb", "nl", "sv"]);
    expect(new Set(manifests.map((m) => m.alias)).size).toBe(manifests.length);
    for (const m of manifests) expect(m.type).toBe("localization");
  });
});

describe("every key the code asks for exists in English", () => {
  const sourceDir = fileURLToPath(new URL("../src/", import.meta.url));
  const files: string[] = [];
  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === "api" || entry.name === "node_modules") continue;
      if (entry.isDirectory()) walk(dir + entry.name + "/");
      else if (/\.ts$/.test(entry.name) && !dir.endsWith("/localization/")) files.push(dir + entry.name);
    }
  };
  walk(sourceDir);
  const code = files.map((f) => readFileSync(f, "utf8")).join("\n");

  it("finds the literal keys (a guard so this test cannot go quiet)", () => {
    expect(new Set(code.match(/bluebillywig_[A-Za-z0-9]+/g)).size).toBeGreaterThan(100);
  });

  it("has a term for each literal key", () => {
    const used = [...new Set(code.match(/bluebillywig_[A-Za-z0-9]+/g))];
    // Keys that are a prefix: the code appends a suffix to build the key (status + Published, media + Video, noun + Videos).
    const prefixes = ["bluebillywig_status", "bluebillywig_media", "bluebillywig_noun"];
    const missing = used.filter((k) => !(k in source) && !prefixes.includes(k));
    expect(missing).toEqual([]);
  });

  it("has the terms behind the keys the code builds from a suffix", () => {
    for (const suffix of ["Published", "Draft", "Archived"]) expect(source["bluebillywig_status" + suffix]).toBeDefined();
    for (const suffix of ["All", "Video", "Audio", "Image", "Document"]) expect(source["bluebillywig_media" + suffix]).toBeDefined();
    for (const what of ["videos", "livestreams", "folders", "playouts", "channels", "shorts", "clips", "bulk action", "cache clearing", "cleanup", "change"]) {
      const key = "bluebillywig_noun" + what.split(" ").map((w) => w[0].toUpperCase() + w.slice(1)).join("");
      expect(source[key], key).toBeDefined();
    }
  });

  it("registers the manifest labels as # keys that exist", () => {
    const labels = [...new Set(code.match(/"#bluebillywig_[A-Za-z0-9]+"/g))].map((l) => l.slice(2, -1));
    expect(labels.length).toBeGreaterThanOrEqual(5);
    for (const key of labels) expect(source[key], key).toBeDefined();
  });
});

describe("English fallback", () => {
  it("englishTerm gives the English text, with arguments, and the key itself for an unknown key", () => {
    expect(englishTerm("bluebillywig_save")).toBe("Save");
    expect(englishTerm("bluebillywig_connectedTo", "Acme")).toBe("Connected to Acme");
    expect(englishTerm("bluebillywig_noSuchKey")).toBe("bluebillywig_noSuchKey");
  });

  it("a culture without a key (or with an empty value) reads English, never the key", () => {
    const t = termFor({ bluebillywig_save: "Opslaan", bluebillywig_cancel: "" });
    expect(t("bluebillywig_save")).toBe("Opslaan");
    expect(t("bluebillywig_cancel")).toBe("Cancel");
    expect(t("bluebillywig_remove")).toBe("Remove");
    expect(t("bluebillywig_connectedTo", "Acme")).toBe("Connected to Acme");
  });

  it("an empty culture reads exactly like English", () => {
    const t = termFor({});
    for (const key of Object.keys(source)) expect(t(key, "a", "b"), key).toBe(englishTerm(key, "a", "b"));
  });

  it("interpolate fills {n} and %n%, and leaves a marker whose argument is missing", () => {
    expect(interpolate("{1} of {0}", ["a", "b"])).toBe("b of a");
    expect(interpolate("%0% / {2}", ["x"])).toBe("x / {2}");
    expect(interpolate("no markers {0}", [])).toBe("no markers {0}");
  });
});

describe("pure helpers take a translator", () => {
  const nl = termFor(flatten(locales.nl));

  it("libraryError and saveError use the sentence and the noun of the culture", () => {
    expect(libraryError({ status: 401 }, "videos", nl)).toBe("Je hebt geen toegang tot video’s.");
    expect(saveError({ status: 404 }, "change", nl)).toBe("Deze clip bestaat niet meer.");
  });

  it("an English noun without a key is used as written", () => {
    expect(libraryError({ status: 500 }, "platform details", nl)).toBe("Kon platform details niet laden.");
    expect(nounOf("platform details", nl)).toBe("platform details");
  });

  it("limitsText uses the culture's sentence", () => {
    expect(limitsText(nl)).toContain("Eén bestand tegelijk, tot 20 GB.");
  });
});

describe("visitor text files", () => {
  const dir = fileURLToPath(new URL("../../Localization/Visitor/", import.meta.url));
  const load = (name: string): Record<string, string> => JSON.parse(readFileSync(dir + name, "utf8"));
  const english = load("en.json");

  it("exist for en, da, de, fi, fr, nb, nl, sv and nothing else", () => {
    expect(readdirSync(dir).sort()).toEqual(["da.json", "de.json", "en.json", "fi.json", "fr.json", "nb.json", "nl.json", "sv.json"]);
  });

  for (const culture of Object.keys(locales)) {
    it(`${culture} has exactly the English keys, no empty values, the same placeholders`, () => {
      const texts = load(`${culture}.json`);
      expect(Object.keys(texts).sort()).toEqual(Object.keys(english).sort());
      for (const [key, value] of Object.entries(texts)) {
        expect(value.trim(), key).not.toBe("");
        expect(placeholders(value), key).toEqual(placeholders(english[key]));
      }
    });
  }

  it("holds the keys the scripts and the renderer ask for", () => {
    const renderer = readFileSync(fileURLToPath(new URL("../../Embedding/BlueBillywigEmbedRenderer.cs", import.meta.url)), "utf8");
    const asked = [...renderer.matchAll(/\bV\("([A-Za-z]+)"/g)].map((m) => m[1]);
    expect(asked.length).toBeGreaterThan(10);
    for (const key of asked) expect(english[key], key).toBeDefined();
  });
});
