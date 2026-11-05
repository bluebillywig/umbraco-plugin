import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getLibraryPlaylists, type LibraryPlaylistListResponseModel } from "../api/index.js";
import { formatDate, libraryError } from "./errors.js";
import "../shared/credentials-banner.element.js";

/** The Playlists section view. Each playlist has an embed code to copy. */
@customElement("bb-library-playlists")
export class BbLibraryPlaylistsElement extends UmbElementMixin(LitElement) {
  @state() private _search = "";
  @state() private _page = 0;
  @state() private _result?: LibraryPlaylistListResponseModel;
  @state() private _loading = false;
  @state() private _error?: string;
  @state() private _copied?: number;

  #timer?: ReturnType<typeof setTimeout>;
  #seq = 0;

  override connectedCallback() {
    super.connectedCallback();
    void this.#load();
  }

  async #load() {
    const seq = ++this.#seq;
    this._loading = true;
    this._error = undefined;
    const { data, error } = await tryExecute(this, getLibraryPlaylists({ query: { search: this._search || undefined, page: this._page } }));
    if (seq !== this.#seq) return;
    this._loading = false;
    if (error || !data) {
      this._result = undefined;
      this._error = libraryError(error, "playlists");
      return;
    }
    this._result = data;
  }

  async #copy(id: number, code: string) {
    await navigator.clipboard.writeText(code);
    this._copied = id;
    setTimeout(() => (this._copied = undefined), 1500);
  }

  #body() {
    if (this._error) {
      return html`<div class="error" id="bb-error" role="alert"><strong>Error</strong><p>${this._error}</p>
        <uui-button label="Try again" look="primary" color="danger" @click=${() => void this.#load()}>Try again</uui-button></div>`;
    }
    if (this._loading && !this._result) return html`<uui-loader-bar></uui-loader-bar>`;
    const items = this._result?.items ?? [];
    if (items.length === 0) return html`<p class="empty" id="bb-empty">No playlists found.</p>`;
    return html`<uui-table>
        <uui-table-head>
          <uui-table-head-cell>Title</uui-table-head-cell>
          <uui-table-head-cell>Status</uui-table-head-cell>
          <uui-table-head-cell>Created</uui-table-head-cell>
          <uui-table-head-cell>Embed code</uui-table-head-cell>
        </uui-table-head>
        ${items.map(
          (p) => html`<uui-table-row data-playlist-id=${p.id}>
            <uui-table-cell><strong>${p.title}</strong><br /><small>ID ${p.id}</small></uui-table-cell>
            <uui-table-cell>${p.status ?? ""}</uui-table-cell>
            <uui-table-cell>${formatDate(p.createdDate)}</uui-table-cell>
            <uui-table-cell>
              <uui-input label="Embed code" readonly .value=${p.embedCode}></uui-input>
              <uui-button label="Copy" look="secondary" compact @click=${() => void this.#copy(p.id, p.embedCode)}>${this._copied === p.id ? "Copied" : "Copy"}</uui-button>
            </uui-table-cell>
          </uui-table-row>`,
        )}
      </uui-table>
      ${this._result && this._result.total > this._result.pageSize
        ? html`<uui-pagination
            .total=${Math.ceil(this._result.total / this._result.pageSize)}
            .current=${this._page + 1}
            @change=${(e: Event) => {
              this._page = (e.target as HTMLElement & { current: number }).current - 1;
              void this.#load();
            }}></uui-pagination>`
        : nothing}`;
  }

  override render() {
    return html`<bb-credentials-banner></bb-credentials-banner><uui-box headline=${this.localize.term("bluebillywig_tabPlaylists")}>
      <div class="filters">
        <uui-input
          id="bb-search"
          label="Search"
          placeholder="Search playlists by title"
          .value=${this._search}
          @input=${(e: Event) => {
            this._search = (e.target as HTMLInputElement).value;
            this._page = 0;
            clearTimeout(this.#timer);
            this.#timer = setTimeout(() => void this.#load(), 300);
          }}></uui-input>
        <span class="count" id="bb-count">${this._result ? `${this._result.total} items` : ""}</span>
      </div>
      ${this.#body()}
    </uui-box>`;
  }

  static override styles = css`
    :host { display: block; padding: var(--uui-size-layout-1); }
    .filters { display: flex; gap: var(--uui-size-space-3); align-items: center; margin-bottom: var(--uui-size-space-4); }
    .filters uui-input { flex: 1; }
    .count { color: var(--uui-color-text-alt); }
    uui-table-cell uui-input { width: 70%; }
    .empty { color: var(--uui-color-text-alt); padding: var(--uui-size-space-5); }
    .error { border: 1px solid var(--uui-color-danger); border-radius: var(--uui-border-radius); padding: var(--uui-size-space-4); color: var(--uui-color-danger-standalone); }
    uui-pagination { display: block; margin-top: var(--uui-size-space-5); }
  `;
}

export default BbLibraryPlaylistsElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-library-playlists": BbLibraryPlaylistsElement;
  }
}
