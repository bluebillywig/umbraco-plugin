import { describe, expect, it } from "vitest";
import { b64, el, FakeNode, fragment, runPublicScript, scriptTag } from "./helpers/fake-dom";

// Client/public/bb-lazy.js is a plain script. Part 1 loads it without a document, which is how it exposes its three pure
// helpers. Part 2 loads it with a hand-written fake document and drives the security rules through its real code paths.

describe("bb-lazy.js host rules", () => {
  const { parseHostList, hostAllowed, urlAllowed } = (() => {
    const sandbox = runPublicScript("bb-lazy.js", { module: { exports: {} } });
    return sandbox.module.exports as Record<string, (...a: any[]) => any>;
  })();

  it("splits the wrapper's host list on whitespace and lower-cases it", () => {
    expect(Array.from(parseHostList("  Player.Example.com   *.CDN.example.net "))).toEqual(["player.example.com", "*.cdn.example.net"]);
    expect(Array.from(parseHostList(null))).toEqual([]);
  });

  it("matches an exact host", () => {
    const list = parseHostList("player.example.com");
    expect(hostAllowed("player.example.com", list)).toBe(true);
    expect(hostAllowed("PLAYER.example.com", list)).toBe(true);
    expect(hostAllowed("other.example.com", list)).toBe(false);
  });

  it("matches a wildcard entry as the domain and its subdomains, not a look-alike", () => {
    const list = parseHostList("*.example.com");
    expect(hostAllowed("example.com", list)).toBe(true);
    expect(hostAllowed("a.example.com", list)).toBe(true);
    expect(hostAllowed("a.b.example.com", list)).toBe(true);
    expect(hostAllowed("evilexample.com", list)).toBe(false);
    expect(hostAllowed("example.com.evil.test", list)).toBe(false);
  });

  it("refuses everything for an empty host or an empty list", () => {
    expect(hostAllowed("", parseHostList("*.example.com"))).toBe(false);
    expect(hostAllowed("example.com", [])).toBe(false);
  });

  it("allows only https URLs on a listed host, without credentials", () => {
    const list = parseHostList("*.example.com");
    expect(urlAllowed("https://player.example.com/x.js", list)).toBe(true);
    expect(urlAllowed("http://player.example.com/x.js", list)).toBe(false);
    expect(urlAllowed("//player.example.com/x.js", list)).toBe(false);
    expect(urlAllowed("https://user:pw@player.example.com/x.js", list)).toBe(false);
    expect(urlAllowed("https://evil.test/x.js", list)).toBe(false);
    expect(urlAllowed("javascript:alert(1)", list)).toBe(false);
    expect(urlAllowed("data:text/html,x", list)).toBe(false);
    expect(urlAllowed("not a url", list)).toBe(false);
  });
});

// ---------------------------------------------------------------------------------------------------------------------

const KEY = "k3y-ABC_123";
const HOSTS = "player.example.com *.cdn.example.net";

interface Boot {
  queries: string[];
  observed: FakeNode[];
  fire(target: FakeNode): void;
  parsed: string[];
  created: FakeNode[];
}

/** Loads bb-lazy.js against a fake document. `parse` stands in for the browser's HTML parser: it receives the decoded text. */
function boot(key: string | null, wrappers: FakeNode[], parse: (html: string) => FakeNode[] = () => [], text?: Record<string, string> | string): Boot {
  const queries: string[] = [];
  const observed: FakeNode[] = [];
  const parsed: string[] = [];
  const created: FakeNode[] = [];
  let callback: (entries: unknown[]) => void = () => {};

  class FakeObserver {
    constructor(cb: (entries: unknown[]) => void) { callback = cb; }
    observe(target: FakeNode) { observed.push(target); }
    unobserve() {}
  }

  const document = {
    currentScript: scriptTag(key, text),
    cookie: "",
    addEventListener() {},
    querySelectorAll(selector: string) { queries.push(selector); return wrappers; },
    createElement(tag: string) {
      if (tag === "template") {
        const content = fragment();
        return { content, set innerHTML(html: string) { parsed.push(html); for (const n of parse(html)) content.appendChild(n); } };
      }
      const node = el(tag);
      created.push(node);
      return node;
    },
  };

  runPublicScript("bb-lazy.js", { document, IntersectionObserver: FakeObserver, location: { protocol: "https:" } });
  return { queries, observed, parsed, created, fire: (target) => callback([{ isIntersecting: true, target }]) };
}

function wrapper(html: string | null, attrs: Record<string, string> = {}): FakeNode {
  const w = el("div", { "data-bb-key": KEY, "data-bb-hosts": HOSTS, ...(html === null ? {} : { "data-bb-embed": b64(html) }), ...attrs });
  w.className = "bb-lazy-embed bb-lazy-ratio-16-9";
  el("div", {}, w); // the parent the loader sizes
  return w;
}

const srcs = (n: FakeNode) => n.children.map((c) => `${c.tagName}:${c.getAttribute("src")}`);

describe("bb-lazy.js trust", () => {
  it("looks only for wrappers that carry the key printed on its own script tag", () => {
    const w = wrapper("<div></div>");
    const { queries, observed } = boot(KEY, [w]);

    expect(queries).toEqual([`.bb-lazy-embed[data-bb-key="${KEY}"]`]);
    expect(observed).toEqual([w]);
  });

  it("strips anything outside the key alphabet before it builds the selector", () => {
    const { queries } = boot('a"] , body [x="', []);
    expect(queries[0]).toBe('.bb-lazy-embed[data-bb-key="abodyx"]');
  });

  it("acts on nothing when its own script tag has no key (a forged or foreign include)", () => {
    const w = wrapper("<div></div>");
    const { queries, observed } = boot(null, [w]);

    expect(queries).toEqual([]);
    expect(observed).toEqual([]);
  });

  it("leaves a wrapper with the wrong or no key exactly as it is, even if it is handed to the activator", () => {
    for (const forged of [wrapper("<div>x</div>", { "data-bb-key": "other" }), (() => { const w = wrapper("<div>x</div>"); w.removeAttribute("data-bb-key"); return w; })()]) {
      const { fire, parsed } = boot(KEY, []);
      forged.reads = [];
      fire(forged);

      expect(forged.replacedWith).toBeNull();
      expect(forged.hasAttribute("data-bb-activated")).toBe(false);
      expect(forged.reads).not.toContain("data-bb-embed"); // never even decoded
      expect(parsed).toEqual([]);
    }
  });

  it("decodes the embed as UTF-8, not Latin-1", () => {
    const html = "<div>café € 日本語 😀</div>";
    const w = wrapper(html);
    const { fire, parsed } = boot(KEY, [w], () => [el("div")]);
    fire(w);

    expect(parsed).toEqual([html]);
  });
});

describe("bb-lazy.js rebuild", () => {
  function run(children: FakeNode[], opts: { noscript?: string } = {}) {
    const w = wrapper("<ignored>");
    const root = el("div", {}, ...children);
    const harness = boot(KEY, [w], () => [root]);
    if (opts.noscript !== undefined) {
      const sibling = { textContent: opts.noscript };
      (w.parentNode as any).querySelector = () => sibling;
    }
    harness.fire(w);
    return { w, root, ...harness };
  }

  it("rebuilds an https script on a listed host as a new script element with the same attributes", () => {
    const original = el("script", { src: "https://player.example.com/embed.js", nonce: "n0nce", async: "" });
    const { w, root, created } = run([original]);

    expect(w.replacedWith).toBe(root);
    const fresh = root.children[0];
    expect(fresh).not.toBe(original); // the parser's inert one is replaced
    expect(created).toContain(fresh);
    expect(fresh.getAttribute("src")).toBe("https://player.example.com/embed.js");
    expect(fresh.getAttribute("nonce")).toBe("n0nce");
  });

  it("allows a wildcard host from the wrapper's list", () => {
    const { root } = run([el("script", { src: "https://a.cdn.example.net/x.js" })]);
    expect(srcs(root)).toEqual(["SCRIPT:https://a.cdn.example.net/x.js"]);
  });

  it("drops a script whose src is http, off the list, protocol-relative or credentialed", () => {
    const { root } = run([
      el("script", { src: "http://player.example.com/embed.js" }),
      el("script", { src: "https://evil.test/embed.js" }),
      el("script", { src: "//player.example.com/embed.js" }),
      el("script", { src: "https://u:p@player.example.com/embed.js" }),
      el("script", { src: "javascript:alert(1)" }),
    ]);
    expect(root.children).toEqual([]);
  });

  it("never rebuilds an inline script", () => {
    const inline = el("script", {});
    inline.textContent = "alert(document.cookie)";
    const { root } = run([inline, el("script", { src: "https://player.example.com/ok.js" })]);

    expect(srcs(root)).toEqual(["SCRIPT:https://player.example.com/ok.js"]);
  });

  it("keeps an iframe only on https and a listed host", () => {
    const { root } = run([
      el("iframe", { src: "https://player.example.com/frame" }),
      el("iframe", { src: "http://player.example.com/frame" }),
      el("iframe", { src: "https://evil.test/frame" }),
      el("iframe", {}),
    ]);
    expect(srcs(root)).toEqual(["IFRAME:https://player.example.com/frame"]);
  });

  it("removes tags the server does not allow", () => {
    const { root } = run([el("img", { src: "x" }), el("style"), el("object"), el("form"), el("div", {}, el("svg"))]);
    expect(root.children.map((c) => c.tagName)).toEqual(["DIV"]);
    expect(root.children[0].children).toEqual([]);
  });

  it("removes event handler attributes, srcdoc and data-bb-* attributes, also on nested elements", () => {
    const frame = el("iframe", { src: "https://player.example.com/f", onload: "x()", srcdoc: "<script>1</script>", "data-bb-key": KEY, title: "Video" });
    const inner = el("span", { onclick: "x()", OnMouseOver: "x()", "data-bb-embed": "AA==", class: "keep" });
    const { root } = run([el("div", { onclick: "x()" }, inner), frame]);

    const [div, kept] = root.children;
    expect(div.attributes).toEqual([]);
    expect(div.children[0].attributes.map((a) => a.name)).toEqual(["class"]);
    expect(kept.attributes.map((a) => a.name).sort()).toEqual(["src", "title"]);
  });

  it("removes an href that is not http(s) or a fragment", () => {
    const { root } = run([
      el("a", { href: "javascript:alert(1)" }),
      el("a", { href: "  data:text/html,x" }),
      el("a", { href: "https://example.com/" }),
      el("a", { href: "#chapters" }),
    ]);
    expect(root.children.map((a) => a.getAttribute("href"))).toEqual([null, null, "https://example.com/", "#chapters"]);
  });

  it("wraps several top-level nodes in one div", () => {
    const w2 = wrapper("<ignored>");
    const h = boot(KEY, [w2], () => [el("div"), el("span")]);
    h.fire(w2);
    expect(w2.replacedWith!.tagName).toBe("DIV");
    expect(w2.replacedWith!.children.map((c) => c.tagName)).toEqual(["DIV", "SPAN"]);
  });

  it("sizes the parent for a 16:9 placeholder", () => {
    const { w } = run([el("div")]);
    const parent = w.parentElement!;
    expect(parent.style.aspectRatio).toBe("16 / 9");
    expect(parent.style.position).toBe("relative");
  });
});

describe("bb-lazy.js failure paths", () => {
  it("shows the noscript copy without any script when the embed cannot be decoded", () => {
    const w = wrapper(null, { "data-bb-embed": "!!!not base64!!!" });
    const copy = el("div", {}, el("script", { src: "https://player.example.com/x.js" }), el("a", { href: "https://example.com/v" }));
    const h = boot(KEY, [w], () => [copy]);
    (w.parentNode as any).querySelector = () => ({ textContent: "<div>fallback</div>" });
    h.fire(w);

    expect(h.parsed).toEqual(["<div>fallback</div>"]);
    expect(w.replacedWith).toBe(copy);
    expect(copy.children.map((c) => c.tagName)).toEqual(["A"]); // withScripts = false
  });

  it("shows a plain message when there is no noscript copy either, and marks the placeholder done", () => {
    const w = wrapper(null, { "data-bb-embed": "!!!not base64!!!" });
    const h = boot(KEY, [w]);
    h.fire(w);

    expect(w.replacedWith).toBeNull();
    expect(w.getAttribute("data-bb-activated")).toBe("1");
    expect(w.getAttribute("aria-label")).toContain("Video unavailable");
  });

  it("says the failure in the language the renderer printed on the script tag", () => {
    const w = wrapper(null, { "data-bb-embed": "!!!not base64!!!" });
    boot(KEY, [w], () => [], { videoUnavailable: "Video niet beschikbaar — probeer de pagina opnieuw te laden." }).fire(w);

    expect(w.getAttribute("aria-label")).toBe("Video niet beschikbaar — probeer de pagina opnieuw te laden.");
  });

  it("falls back to English when the texts are missing, broken, or lack the key or hold an empty value", () => {
    for (const text of [undefined, "{not json", "[]", {}, { videoUnavailable: "" }, { videoUnavailable: 5 as unknown as string }]) {
      const w = wrapper(null, { "data-bb-embed": "!!!not base64!!!" });
      boot(KEY, [w], () => [], text).fire(w);

      expect(w.getAttribute("aria-label")).toBe("Video unavailable — try reloading the page.");
    }
  });

  it("marks a wrapper with no embed data as done without touching it", () => {
    const w = wrapper(null);
    const h = boot(KEY, [w]);
    h.fire(w);

    expect(w.getAttribute("data-bb-activated")).toBe("1");
    expect(w.replacedWith).toBeNull();
  });

  it("does not activate a wrapper twice", () => {
    const w = wrapper("<div></div>");
    const h = boot(KEY, [w], () => [el("div")]);
    h.fire(w);
    h.fire(w);

    expect(h.parsed).toHaveLength(1);
  });
});
