import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, expect, it } from "vitest";
import { openingTags } from "./helpers/templates";

// Umbraco's UI library logs "needs a `label`" for these when they render without an accessible name, and a screen
// reader has nothing to announce for them either.
const NEEDS_NAME = ["uui-input", "uui-select", "uui-textarea", "uui-toggle", "uui-checkbox"];

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === "api" ? [] : sources(path);
    return path.endsWith(".ts") ? [path] : [];
  });
}

describe("accessible names", () => {
  it("finds the opening tag across lines and nested template expressions", () => {
    const text = 'html`<uui-input\n  a=${x ? `${y}>` : ""}\n  @input=${(e: Event) => f(e)}></uui-input>`';
    expect(openingTags(text, "uui-input")).toHaveLength(1);
    expect(openingTags(text, "uui-input")[0]).toContain("@input=");
  });

  it("every uui-input, uui-select, uui-textarea, uui-toggle and uui-checkbox in the sources has a label or aria-label", () => {
    const root = join(__dirname, "..", "src");
    const missing: string[] = [];
    for (const file of sources(root)) {
      const text = readFileSync(file, "utf8");
      for (const tag of NEEDS_NAME) {
        for (const opening of openingTags(text, tag)) {
          if (!/(^|\s)(aria-)?label=/.test(opening)) {
            const line = text.slice(0, text.indexOf(opening)).split("\n").length;
            missing.push(`${relative(root, file)}:${line} <${tag}>`);
          }
        }
      }
    }
    expect(missing).toEqual([]);
  });
});
