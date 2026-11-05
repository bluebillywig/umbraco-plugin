import { css, customElement, html, LitElement, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getLibraryTags } from "../api/index.js";

const SUGGEST_DEBOUNCE_MS = 200;
const MAX_SUGGESTIONS = 10;

/**
 * Tags as chips with autocomplete. Port of bb-tag-chips.js: existing tags are pills with an X, Enter or a comma turns the
 * text into a chip, Backspace in an empty box removes the last chip, and the dropdown suggests the publication's tags
 * (<c>library/tags?q=</c>, which asks SAPI for the tags in use). Fires <c>change</c> with the new list in <c>tags</c>.
 */
@customElement("bb-tag-chips")
export class BbTagChipsElement extends UmbElementMixin(LitElement) {
  @property({ attribute: false }) tags: string[] = [];

  @state() private _input = "";
  @state() private _suggestions: string[] = [];
  @state() private _active = -1;

  #timer?: ReturnType<typeof setTimeout>;
  #seq = 0;

  #emit(tags: string[]) {
    this.tags = tags;
    this.dispatchEvent(new CustomEvent("change", { detail: tags, bubbles: true, composed: true }));
  }

  #add(raw: string) {
    const tag = raw.trim().replace(/,+$/, "").trim();
    if (!tag || this.tags.some((t) => t.toLowerCase() === tag.toLowerCase())) return;
    this.#emit([...this.tags, tag]);
  }

  #remove(index: number) {
    this.#emit(this.tags.filter((_, i) => i !== index));
  }

  #reset() {
    this._input = "";
    this._suggestions = [];
    this._active = -1;
  }

  #onInput(e: Event) {
    const value = (e.target as HTMLInputElement).value;
    if (value.includes(",")) {
      const parts = value.split(",");
      const rest = parts.pop() ?? "";
      parts.forEach((p) => this.#add(p));
      this._input = rest;
      (e.target as HTMLInputElement).value = rest;
    } else {
      this._input = value;
    }
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => void this.#suggest(this._input.trim()), SUGGEST_DEBOUNCE_MS);
  }

  async #suggest(q: string) {
    const seq = ++this.#seq;
    if (!q) {
      this._suggestions = [];
      return;
    }
    const { data } = await tryExecute(this, getLibraryTags({ query: { q } }));
    // A late answer for an older query must not replace a newer one.
    if (seq !== this.#seq) return;
    const have = new Set(this.tags.map((t) => t.toLowerCase()));
    this._suggestions = (data ?? []).filter((t) => !have.has(t.toLowerCase())).slice(0, MAX_SUGGESTIONS);
    this._active = -1;
  }

  #onKey(e: KeyboardEvent) {
    if (e.key === "Enter") {
      e.preventDefault();
      const picked = this._active >= 0 ? this._suggestions[this._active] : this._input;
      this.#add(picked);
      this.#reset();
    } else if (e.key === "Backspace" && this._input === "" && this.tags.length > 0) {
      this.#remove(this.tags.length - 1);
    } else if (e.key === "ArrowDown" && this._suggestions.length > 0) {
      e.preventDefault();
      this._active = (this._active + 1) % this._suggestions.length;
    } else if (e.key === "ArrowUp" && this._suggestions.length > 0) {
      e.preventDefault();
      this._active = (this._active - 1 + this._suggestions.length) % this._suggestions.length;
    } else if (e.key === "Escape") {
      this._suggestions = [];
    }
  }

  #pick(tag: string) {
    this.#add(tag);
    this.#reset();
  }

  override render() {
    return html`<div class="box">
      ${this.tags.map(
        (tag, i) =>
          html`<span class="chip" data-tag=${tag}>${tag}<button type="button" aria-label=${`Remove tag ${tag}`} @click=${() => this.#remove(i)}>×</button></span>`,
      )}
      <input
        id="bb-tag-input"
        type="text"
        autocomplete="off"
        placeholder=${this.tags.length === 0 ? "Add a tag" : ""}
        aria-label="Add a tag"
        .value=${this._input}
        @input=${(e: Event) => this.#onInput(e)}
        @keydown=${(e: KeyboardEvent) => this.#onKey(e)}
        @blur=${() => {
          // Text left in the box when the editor moves on becomes a tag, so it is not lost silently.
          if (this._input.trim()) this.#add(this._input);
          setTimeout(() => this.#reset(), 150);
        }} />
      ${this._suggestions.length > 0
        ? html`<ul class="suggestions" role="listbox" id="bb-tag-suggestions">
            ${this._suggestions.map(
              (s, i) =>
                html`<li role="option" aria-selected=${i === this._active ? "true" : "false"} class=${i === this._active ? "active" : ""} @mousedown=${(e: Event) => { e.preventDefault(); this.#pick(s); }}>${s}</li>`,
            )}
          </ul>`
        : nothing}
    </div>`;
  }

  static override styles = css`
    :host { display: block; position: relative; }
    .box { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; min-height: 36px; padding: 4px 8px; border: 1px solid var(--uui-color-border); border-radius: var(--uui-border-radius); background: var(--uui-color-surface); }
    .box:focus-within { border-color: var(--uui-color-focus); }
    .chip { display: inline-flex; align-items: center; gap: 4px; padding: 2px 4px 2px 10px; border-radius: 12px; background: var(--uui-color-surface-emphasis); font-size: 13px; }
    .chip button { border: 0; background: none; cursor: pointer; font-size: 16px; line-height: 1; padding: 0 4px; color: inherit; }
    input { flex: 1; min-width: 120px; border: 0; outline: 0; background: transparent; font: inherit; color: inherit; padding: 4px 0; }
    .suggestions { position: absolute; z-index: 10; left: 0; right: 0; top: 100%; margin: 2px 0 0; padding: 0; list-style: none; background: var(--uui-color-surface); border: 1px solid var(--uui-color-border); border-radius: var(--uui-border-radius); box-shadow: var(--uui-shadow-depth-3); max-height: 220px; overflow: auto; }
    .suggestions li { padding: 6px 10px; cursor: pointer; }
    .suggestions li:hover, .suggestions li.active { background: var(--uui-color-surface-emphasis); }
  `;
}

export default BbTagChipsElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-tag-chips": BbTagChipsElement;
  }
}
