import { css, customElement, html, LitElement, nothing, property } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import type { FolderResponseModel } from "../api/index.js";

/** The publication's folders as a nested list. Picking one filters the clip list; "All videos" clears it. */
@customElement("bb-folder-tree")
export class BbFolderTreeElement extends UmbElementMixin(LitElement) {
  @property({ attribute: false }) folders: FolderResponseModel[] = [];
  @property() active = "";

  #pick(code: string) {
    this.dispatchEvent(new CustomEvent("folder-change", { detail: code, bubbles: true, composed: true }));
  }

  #list(folders: FolderResponseModel[]): unknown {
    if (folders.length === 0) return nothing;
    return html`<ul>
      ${folders.map(
        (f) => html`<li>
          <button type="button" class=${f.code === this.active ? "link active" : "link"} data-folder=${f.code} title=${f.name} @click=${() => this.#pick(f.code)}>
            <uui-icon name="icon-folder"></uui-icon> ${f.name}
          </button>
          ${this.#list(f.children)}
        </li>`,
      )}
    </ul>`;
  }

  override render() {
    return html`<div class="title">${this.localize.term("bluebillywig_folders")}</div>
      <button type="button" class=${this.active === "" ? "link active" : "link"} data-folder="" @click=${() => this.#pick("")}>${this.localize.term("bluebillywig_allVideos")}</button>
      ${this.#list(this.folders)}`;
  }

  static override styles = css`
    :host { display: block; font-size: 13px; }
    .title { font-weight: 600; margin-bottom: var(--uui-size-space-2); }
    ul { list-style: none; margin: 0; padding: 0 0 0 14px; }
    :host > ul { padding-left: 0; }
    .link { display: block; width: 100%; text-align: left; padding: 4px 8px; border: 0; border-radius: 3px; background: none; color: inherit; font: inherit; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .link:hover { background: var(--uui-color-surface-emphasis); }
    .link.active { background: var(--uui-color-selected); color: var(--uui-color-selected-contrast); }
  `;
}

export default BbFolderTreeElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-folder-tree": BbFolderTreeElement;
  }
}
