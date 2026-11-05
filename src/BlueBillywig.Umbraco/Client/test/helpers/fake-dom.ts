// A deliberately tiny, hand-written stand-in for the few DOM and browser globals that Client/public/*.js touch, so the
// plain scripts can be loaded into a Node `vm` context and their security rules exercised without a DOM package.
// It is not a DOM: it implements only what those two scripts call, and what it does not implement throws.
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

export type Attr = { name: string; value: string };

export class FakeNode {
  nodeType = 1;
  childNodes: FakeNode[] = [];
  attributes: Attr[] = [];
  parentNode: FakeNode | null = null;
  nodeValue = "";
  style: Record<string, string> & { getPropertyValue(name: string): string } = Object.assign({}, { getPropertyValue: () => "" }) as never;
  dataset: Record<string, string> = {};
  listeners: Record<string, Array<(e: unknown) => void>> = {};
  closestMap: Record<string, FakeNode | null> = {};
  replacedWith: FakeNode | null = null;
  reads: string[] = [];
  className = "";
  // Plain property bag for whatever the scripts assign (id, type, value, checked, href, ...).
  [key: string]: unknown;

  constructor(public tagName: string) {}

  get children(): FakeNode[] { return this.childNodes.filter((n) => n.nodeType === 1); }
  get firstElementChild(): FakeNode | undefined { return this.children[0]; }
  get parentElement(): FakeNode | null { return this.parentNode && this.parentNode.nodeType === 1 ? this.parentNode : null; }
  get classList() { return { contains: (c: string) => this.className.split(/\s+/).includes(c) }; }

  getAttribute(name: string): string | null {
    this.reads.push(name);
    return this.attributes.find((a) => a.name === name)?.value ?? null;
  }
  hasAttribute(name: string) { return this.attributes.some((a) => a.name === name); }
  setAttribute(name: string, value: string) {
    const existing = this.attributes.find((a) => a.name === name);
    if (existing) existing.value = String(value); else this.attributes.push({ name, value: String(value) });
  }
  removeAttribute(name: string) { this.attributes = this.attributes.filter((a) => a.name !== name); }
  appendChild(child: FakeNode) {
    if (child.nodeType === 11) { for (const c of [...child.childNodes]) this.appendChild(c); return child; }
    child.parentNode?.removeChild(child);
    child.parentNode = this; this.childNodes.push(child); return child;
  }
  removeChild(child: FakeNode) {
    const i = this.childNodes.indexOf(child);
    if (i >= 0) { this.childNodes.splice(i, 1); child.parentNode = null; }
    return child;
  }
  replaceChild(fresh: FakeNode, old: FakeNode) {
    const i = this.childNodes.indexOf(old);
    this.childNodes[i] = fresh; fresh.parentNode = this; old.parentNode = null; return old;
  }
  replaceWith(fresh: FakeNode) { this.replacedWith = fresh; }
  addEventListener(type: string, fn: (e: unknown) => void) { (this.listeners[type] ??= []).push(fn); }
  focus() {}
  closest(selector: string): FakeNode | null { return this.closestMap[selector] ?? null; }
  querySelector(): FakeNode | null { return null; }
  get textContent(): string { return this._text; }
  set textContent(v: string) { this._text = v; }
  _text = "";
  _html = "";
  get innerHTML(): string { return this._html; }
  set innerHTML(v: string) { this._html = v; this.childNodes = []; }
}

export function el(tag: string, attrs: Record<string, string> = {}, ...kids: FakeNode[]): FakeNode {
  const n = new FakeNode(tag.toUpperCase());
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
  for (const k of kids) n.appendChild(k);
  return n;
}

export function text(value: string): FakeNode {
  const n = new FakeNode("#text");
  n.nodeType = 3; n.nodeValue = value;
  return n;
}

export function fragment(): FakeNode {
  const n = new FakeNode("#fragment");
  n.nodeType = 11;
  return n;
}

/** Loads one of Client/public/*.js into a fresh vm context, with the globals given, and returns the context. */
export function runPublicScript(file: string, globals: Record<string, unknown>): Record<string, any> {
  const code = readFileSync(fileURLToPath(new URL(`../../public/${file}`, import.meta.url)), "utf8");
  const sandbox: Record<string, any> = { URL, TextDecoder, atob, console: { error() {}, warn() {}, log() {} }, ...globals };
  sandbox.window = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(code, sandbox, { filename: file });
  return sandbox;
}

/** The script tag the renderer prints: the per-request key, and optionally the translated texts (data-bb-text, JSON). */
export function scriptTag(key: string | null, text?: Record<string, string> | string): FakeNode {
  const attrs: Record<string, string> = key === null ? {} : { "data-bb-key": key };
  if (text !== undefined) attrs["data-bb-text"] = typeof text === "string" ? text : JSON.stringify(text);
  return el("script", attrs);
}

export const b64 = (s: string) => Buffer.from(s, "utf8").toString("base64");
