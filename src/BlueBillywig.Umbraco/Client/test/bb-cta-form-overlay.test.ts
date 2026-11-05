import { describe, expect, it } from "vitest";
import { el, FakeNode, runPublicScript, scriptTag } from "./helpers/fake-dom";

// Client/public/bb-cta-form-overlay.js is a plain script. It is loaded with a hand-written fake document, localStorage,
// fetch and location, and driven through its real upgrade and submit paths. Mirrors the server half in
// tests/BlueBillywig.Umbraco.Tests/Hardening/CtaEndpointTests.cs.

const KEY = "ctaKey-123";
const CONTAINER = `[data-bb-key="${KEY}"][data-bb-clip-id]`;
const DAY = 24 * 60 * 60 * 1000;

const config = (cfg: unknown) => "bb-cta-form-config-" + Buffer.from(JSON.stringify(cfg)).toString("base64url");

interface Fetched { url: string; init: any }

function boot(options: { key?: string | null; pageHost?: string; storage?: Record<string, string>; widgets: FakeNode[]; fetchOk?: boolean; text?: Record<string, string> | string }) {
  const storage: Record<string, string> = options.storage ?? {};
  const created: FakeNode[] = [];
  const fetched: Fetched[] = [];
  const removed: string[] = [];
  const document = {
    currentScript: scriptTag(options.key === undefined ? KEY : options.key, options.text),
    body: new FakeNode("BODY"),
    querySelectorAll: () => options.widgets,
    createElement: (tag: string) => { const n = el(tag); created.push(n); return n; },
    createTextNode: (t: string) => { const n = new FakeNode("#text"); n.nodeType = 3; n.nodeValue = t; return n; },
    addEventListener() {},
  };
  const localStorage = {
    getItem: (k: string) => (k in storage ? storage[k] : null),
    setItem: (k: string, v: string) => { storage[k] = v; },
    removeItem: (k: string) => { removed.push(k); delete storage[k]; },
  };
  const pageHost = options.pageHost ?? "www.example.com";
  runPublicScript("bb-cta-form-overlay.js", {
    document,
    localStorage,
    location: { hostname: pageHost, href: `https://${pageHost}/page?utm=secret` },
    MutationObserver: class { observe() {} },
    fetch: (url: string, init: unknown) => { fetched.push({ url, init }); return Promise.resolve({ ok: options.fetchOk ?? true, status: options.fetchOk === false ? 500 : 200 }); },
  });
  return { storage, created, fetched, removed };
}

/** A `.bb-cta-form` widget inside (or not inside) a player container the renderer made, carrying `cfg` in its class. */
function widget(cfg: unknown, opts: { inContainer?: boolean; clipId?: string } = {}): FakeNode {
  const w = el("div", { "data-bb-clip-id": "666" }); // an attribute on the widget itself must never be trusted
  w.className = `bb-cta-form ${config(cfg)}`;
  if (opts.inContainer !== false) w.closestMap[CONTAINER] = el("div", { "data-bb-key": KEY, "data-bb-clip-id": opts.clipId ?? "42" });
  return w;
}

const find = (created: FakeNode[], tag: string, cls?: string) => created.find((n) => n.tagName === tag.toUpperCase() && (!cls || n.className === cls));

/** Fills and submits the rendered form, then lets the fetch promise chain settle. */
async function submit(created: FakeNode[], email = "a@b.example") {
  find(created, "input", "bb-cta-form__email")!.value = email;
  find(created, "input", "bb-cta-form__consent-checkbox")!.checked = true;
  (find(created, "input", "bb-cta-form__email") as any).checkValidity = () => true;
  let prevented = false;
  find(created, "form")!.listeners.submit[0]({ preventDefault() { prevented = true; } });
  expect(prevented).toBe(true);
  await new Promise((r) => setTimeout(r, 0));
}

describe("bb-cta-form-overlay.js trust", () => {
  it("builds a form only inside a player container that carries the script's key", () => {
    const w = widget({ heading: "Hi" });
    const { created } = boot({ widgets: [w] });

    expect(find(created, "form")).toBeDefined();
    expect(w.dataset.bbCtaFormReady).toBe("1");
  });

  it("leaves a widget outside such a container alone", () => {
    const w = widget({ heading: "Hi" }, { inContainer: false });
    const { created } = boot({ widgets: [w] });

    expect(created).toEqual([]);
    expect(w.dataset.bbCtaFormReady).toBeUndefined();
  });

  it("builds nothing when its own script tag has no key", () => {
    const w = widget({ heading: "Hi" });
    const { created } = boot({ key: null, widgets: [w] });

    expect(created).toEqual([]);
  });

  it("uses the clip id of the container, not an attribute on the widget", async () => {
    const w = widget({ submitEndpoint: "https://crm.example/hook" }, { clipId: "42" });
    const { created, fetched } = boot({ widgets: [w] });
    await submit(created);

    expect(JSON.parse(fetched[0].init.body).clipId).toBe("42");
  });

  it("leaves the widget as it is when its config cannot be decoded", () => {
    const w = widget({});
    w.className = "bb-cta-form bb-cta-form-config-AAAA"; // valid base64 that is not JSON
    const { created } = boot({ widgets: [w] });

    expect(created).toEqual([]);
    expect(w.dataset.bbCtaFormError).toBe("config-decode-failed");
  });
});

describe("bb-cta-form-overlay.js submit endpoint rule", () => {
  async function post(endpoint: string, pageHost = "www.example.com") {
    const { created, fetched, storage } = boot({ pageHost, widgets: [widget({ submitEndpoint: endpoint })] });
    await submit(created);
    return { fetched, storage };
  }

  it.each(["https://crm.example/hook", "https://crm.example:8443/hook?a=1"])("posts to %s", async (url) => {
    expect((await post(url)).fetched.map((f) => f.url)).toEqual([url]);
  });

  it.each([
    "http://crm.example/hook",
    "http://evil.localhost.example/hook",
    "http://localhost.evil.example/hook",
    "http://192.168.1.10/hook",
    "javascript:alert(1)",
    "data:text/plain,x",
    "file:///etc/passwd",
    "ftp://crm.example",
    "//crm.example/hook",
    "https://user:pw@crm.example/hook",
    "http://user:pw@localhost/hook",
  ])("never posts to %s", async (url) => {
    expect((await post(url, "localhost")).fetched).toEqual([]);
  });

  it.each(["http://localhost:3000/hook", "http://shop.localhost/hook", "http://127.0.0.1:5000/x", "http://[::1]:5000/x"])(
    "posts to the local %s only from a local page",
    async (url) => {
      expect((await post(url, "localhost")).fetched.map((f) => f.url)).toEqual([url]);
      expect((await post(url, "www.example.com")).fetched).toEqual([]);
      expect((await post(url, "localhost.evil.example")).fetched).toEqual([]);
    },
  );

  it("sends a minimal JSON payload with no credentials and no page URL", async () => {
    const { fetched } = await post("https://crm.example/hook");
    const [{ init }] = fetched;

    expect(init.method).toBe("POST");
    expect(init.credentials).toBe("omit");
    expect(init.headers).toEqual({ "Content-Type": "application/json" });
    const body = JSON.parse(init.body);
    expect(Object.keys(body).sort()).toEqual(["clipId", "consent", "email", "submittedAt"]);
    expect(body).toMatchObject({ email: "a@b.example", clipId: "42", consent: true });
    expect(new Date(body.submittedAt).toISOString()).toBe(body.submittedAt);
  });

  it("adds the page URL only when the config asks for it", async () => {
    const { created, fetched } = boot({ widgets: [widget({ submitEndpoint: "https://crm.example/hook", includePostUrl: true })] });
    await submit(created);

    expect(JSON.parse(fetched[0].init.body).postUrl).toBe("https://www.example.com/page?utm=secret");
  });

  it("does not remember the submission when the endpoint answers an error, and lets the visitor retry", async () => {
    const { created, storage } = boot({ widgets: [widget({ submitEndpoint: "https://crm.example/hook" })], fetchOk: false });
    await submit(created);

    expect(storage).toEqual({});
    expect(find(created, "button")!.disabled).toBe(false);
    expect(find(created, "div", "bb-cta-form__error-host")!.textContent).toContain("Could not submit");
  });

  it("uses the printed texts for the defaults, and the editor's own labels over them", () => {
    const text = { ctaSubscribe: "Aanmelden", ctaEmail: "E-mailadres", ctaConsent: "Ik ga akkoord.", ctaPrivacy: "Privacybeleid", ctaSubmit: "Verzenden" };
    const { created } = boot({ widgets: [widget({ privacyPolicyUrl: "https://example.com/privacy", submitLabel: "Stuur" })], text });

    expect(find(created, "h3")!.textContent).toBe("Aanmelden");
    expect(find(created, "label", "bb-cta-form__email-label")!.textContent).toBe("E-mailadres");
    expect(find(created, "span", "bb-cta-form__consent-text")!.textContent).toBe("Ik ga akkoord.");
    expect(find(created, "a", "bb-cta-form__privacy-link")!.textContent).toBe("Privacybeleid");
    expect(find(created, "button")!.textContent).toBe("Stuur");
  });

  it("shows the printed validation messages, and English ones without them", () => {
    for (const [text, expected] of [[{ ctaErrEmail: "Voer een geldig e-mailadres in." }, "Voer een geldig e-mailadres in."], [undefined, "Please enter a valid email address."], ["{broken", "Please enter a valid email address."]] as const) {
      const { created } = boot({ widgets: [widget({})], text });
      find(created, "input", "bb-cta-form__email")!.value = "";
      find(created, "form")!.listeners.submit[0]({ preventDefault() {} });

      expect(find(created, "div", "bb-cta-form__error-host")!.textContent).toBe(expected);
    }
  });

  it("does not submit without a valid email or without consent", async () => {
    const { created, fetched } = boot({ widgets: [widget({ submitEndpoint: "https://crm.example/hook" })] });
    const email = find(created, "input", "bb-cta-form__email")!;
    const form = find(created, "form")!;

    email.value = "";
    form.listeners.submit[0]({ preventDefault() {} });
    expect(find(created, "div", "bb-cta-form__error-host")!.textContent).toContain("valid email");

    email.value = "a@b.example";
    (email as any).checkValidity = () => true;
    form.listeners.submit[0]({ preventDefault() {} });
    expect(find(created, "div", "bb-cta-form__error-host")!.textContent).toContain("consent");

    await Promise.resolve();
    expect(fetched).toEqual([]);
  });
});

describe("bb-cta-form-overlay.js privacy link", () => {
  it("shows an https privacy link with rel noopener noreferrer", () => {
    const { created } = boot({ widgets: [widget({ privacyPolicyUrl: "https://example.com/privacy" })] });
    const link = find(created, "a")!;

    expect(link.href).toBe("https://example.com/privacy");
    expect(link.rel).toBe("noopener noreferrer");
  });

  it.each(["http://example.com/privacy", "javascript:alert(1)"])("drops %s", (url) => {
    const { created } = boot({ widgets: [widget({ privacyPolicyUrl: url })] });

    expect(find(created, "a")).toBeUndefined();
  });
});

describe("bb-cta-form-overlay.js remembered submissions (localStorage)", () => {
  it("stores {email, submittedAt} under bbtl_<clipId> after a successful submit", async () => {
    const { created, storage } = boot({ widgets: [widget({ submitEndpoint: "https://crm.example/hook" })] });
    await submit(created, "me@example.com");

    expect(Object.keys(storage)).toEqual(["bbtl_42"]);
    const entry = JSON.parse(storage.bbtl_42);
    expect(Object.keys(entry).sort()).toEqual(["email", "submittedAt"]);
    expect(entry.email).toBe("me@example.com");
    expect(new Date(entry.submittedAt).toISOString()).toBe(entry.submittedAt);
  });

  it("escapes the thank-you text", async () => {
    const w = widget({ thankYou: '<img src=x onerror="alert(1)">' });
    const { created } = boot({ widgets: [w] });
    await submit(created);

    expect(w.innerHTML).toContain("&lt;img src=x onerror=&quot;alert(1)&quot;&gt;");
    expect(w.innerHTML).not.toContain("<img");
  });

  it("hides the widget for a visitor who submitted within the year, and builds no form", () => {
    const w = widget({ heading: "Hi" });
    const stored = JSON.stringify({ email: "a@b.example", submittedAt: new Date(Date.now() - 30 * DAY).toISOString() });
    const { created, removed } = boot({ widgets: [w], storage: { bbtl_42: stored } });

    expect(w.style.display).toBe("none");
    expect(created).toEqual([]);
    expect(removed).toEqual([]);
  });

  it("drops an entry older than a year and shows the form again", () => {
    const w = widget({ heading: "Hi" });
    const stored = JSON.stringify({ email: "a@b.example", submittedAt: new Date(Date.now() - 400 * DAY).toISOString() });
    const { created, removed, storage } = boot({ widgets: [w], storage: { bbtl_42: stored } });

    expect(removed).toEqual(["bbtl_42"]);
    expect(storage).toEqual({});
    expect(find(created, "form")).toBeDefined();
    expect(w.style.display).toBeUndefined();
  });

  it("counts an entry in an older or unreadable shape as submitted (only the key's presence matters)", () => {
    for (const raw of ["1", "not json", JSON.stringify({ email: "a@b.example" })]) {
      const w = widget({ heading: "Hi" });
      const { created } = boot({ widgets: [w], storage: { bbtl_42: raw } });

      expect(w.style.display).toBe("none");
      expect(created).toEqual([]);
    }
  });

  it("ignores an entry stored for another clip", () => {
    const stored = JSON.stringify({ email: "a@b.example", submittedAt: new Date().toISOString() });
    const { created } = boot({ widgets: [widget({})], storage: { bbtl_43: stored } });

    expect(find(created, "form")).toBeDefined();
  });
});
