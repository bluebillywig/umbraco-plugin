import { css, customElement, html, LitElement, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import {
  getClips,
  getFolders,
  getLibraryCapabilities,
  getLibraryClips,
  getLibraryFolders,
  getLibraryLivestreams,
  getLibraryPolicies,
  postLibraryClipsBulk,
  postLibraryClipsBulkDelete,
  type BulkResultResponseModel,
  type FolderResponseModel,
  type LibraryClipListResponseModel,
  type LibraryClipResponseModel,
  type PolicyResponseModel,
} from "../api/index.js";
import { libraryError, saveError } from "./errors.js";
import type { Term } from "../localization/terms.js";
import "./clip-tile.element.js";
import "./folder-tree.element.js";
import { hasLiveStreams, listHeadingKey, pickerSourceType, selectFolder, selectLive } from "./picker-source.js";

export type BrowserVariant = "videos" | "livestreams" | "picker";

/** The plugin's cap: a bulk request carries at most this many clips. */
const MAX_BULK = 50;

type BulkAction = "status" | "folder" | "cpp";
const UNSET = "\u0000unset";

function flatten(folders: FolderResponseModel[], depth = 0): Array<{ name: string; value: string }> {
  return folders.flatMap((f) => [{ name: `${"\u00A0\u00A0".repeat(depth)}${f.name}`, value: f.code }, ...flatten(f.children, depth + 1)]);
}

/**
 * The clip grid used by the Videos and Livestreams views and by the picker modal: search, filters, folder tree,
 * pagination, and an error state that is never confused with an empty library.
 * The picker reads through the content-section endpoints, so an editor who may insert a video does not need the
 * Blue Billywig section to find one.
 */
@customElement("bb-clip-browser")
export class BbClipBrowserElement extends UmbElementMixin(LitElement) {
  #t: Term = (key, ...args) => this.localize.term(key, ...args);

  @property() variant: BrowserVariant = "videos";

  @state() private _search = "";
  @state() private _mediaType = "all";
  @state() private _from = "";
  @state() private _to = "";
  @state() private _folder = "";
  @state() private _page = 0;
  @state() private _folders: FolderResponseModel[] = [];
  @state() private _result?: LibraryClipListResponseModel;
  @state() private _loading = false;
  @state() private _error?: string;

  // The picker's live stream toggle (the reference plugin's block picker has one): offered only when the publication has live streams.
  @state() private _hasLive = false;
  @state() private _liveOnly = false;

  // Bulk actions (the videos view only).
  @state() private _selected = new Map<number, string>();
  @state() private _canDelete = false;
  @state() private _policies: PolicyResponseModel[] = [];
  @state() private _bulkAction: BulkAction = "status";
  @state() private _bulkValue = UNSET;
  @state() private _bulkBusy = false;
  @state() private _bulkResult?: BulkResultResponseModel;
  @state() private _bulkError?: string;
  @state() private _bulkTitles = new Map<number, string>();

  #timer?: ReturnType<typeof setTimeout>;
  #seq = 0;

  get #hasFilters() {
    return this.variant !== "livestreams";
  }

  override connectedCallback() {
    super.connectedCallback();
    void this.#loadFolders();
    void this.reload();
    if (this.variant === "videos") void this.#loadBulkLookups();
    if (this.variant === "picker") void this.#loadLive();
  }

  /** One query for live source-type clips; the toggle shows when it finds any. A failure hides the toggle, it never blocks the list. */
  async #loadLive() {
    const { data, error } = await tryExecute(this, getClips({ query: { sourcetype: "live", page: 0 } }));
    this._hasLive = hasLiveStreams(data, error);
  }

  async #loadBulkLookups() {
    const [caps, policies] = await Promise.all([tryExecute(this, getLibraryCapabilities()), tryExecute(this, getLibraryPolicies())]);
    this._canDelete = caps.data?.canDelete === true;
    this._policies = policies.data ?? [];
  }

  async #loadFolders() {
    if (!this.#hasFilters) return;
    const { data, error } = await tryExecute(this, this.variant === "picker" ? getFolders() : getLibraryFolders());
    // A failure here is shown, but the list can still be used without the folder sidebar.
    if (error) this._error = libraryError(error, "folders", this.#t);
    else this._folders = data ?? [];
  }

  /** Loads the current page. Public so the host view can refresh after a delete. */
  async reload() {
    const seq = ++this.#seq;
    this._loading = true;
    this._error = undefined;
    const query = {
      search: this._search || undefined,
      page: this._page,
    };
    const filters = {
      ...query,
      sourcetype: this.variant === "picker" ? pickerSourceType(this._liveOnly) : undefined,
      folder: this._folder || undefined,
      mediatype: this._mediaType === "all" ? undefined : this._mediaType,
      from: this._from || undefined,
      to: this._to || undefined,
    };
    const call =
      this.variant === "picker"
        ? getClips({ query: filters })
        : this.variant === "livestreams"
          ? getLibraryLivestreams({ query })
          : getLibraryClips({ query: filters });
    const { data, error } = await tryExecute(this, call);
    if (seq !== this.#seq) return;
    this._loading = false;
    if (error || !data) {
      // Keep nothing from an earlier answer: a failure must not look like a library.
      this._result = undefined;
      this._error = libraryError(error, this.variant === "livestreams" ? "livestreams" : "videos", this.#t);
      return;
    }
    this._result = data;
  }

  #refilter() {
    this._page = 0;
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => void this.reload(), 300);
  }

  #clear() {
    this._search = "";
    this._mediaType = "all";
    this._from = "";
    this._to = "";
    this._folder = "";
    this._liveOnly = false;
    this._page = 0;
    void this.reload();
  }

  #open(clip: LibraryClipResponseModel) {
    this.dispatchEvent(new CustomEvent("select-clip", { detail: clip, bubbles: true, composed: true }));
  }

  #toggle(clip: LibraryClipResponseModel) {
    const next = new Map(this._selected);
    if (next.has(clip.id)) next.delete(clip.id);
    else if (next.size < MAX_BULK) next.set(clip.id, clip.title);
    this._selected = next;
    this._bulkResult = undefined;
    this._bulkError = undefined;
  }

  #selectPage() {
    const next = new Map(this._selected);
    for (const c of this._result?.items ?? []) {
      if (next.size >= MAX_BULK) break;
      next.set(c.id, c.title);
    }
    this._selected = next;
  }

  #clearSelection() {
    this._selected = new Map();
    this._bulkValue = UNSET;
  }

  #bulkOptions(): Array<{ name: string; value: string }> {
    switch (this._bulkAction) {
      case "status":
        return ["published", "draft", "archived"].map((v) => ({ name: this.#t("bluebillywig_status" + v[0].toUpperCase() + v.slice(1)), value: v }));
      case "folder":
        return [{ name: this.#t("bluebillywig_noFolder"), value: "" }, ...flatten(this._folders)];
      default:
        return [{ name: this.#t("bluebillywig_noPolicy"), value: "" }, ...this._policies.map((p) => ({ name: p.name, value: p.code }))];
    }
  }

  async #applyBulk() {
    if (this._bulkValue === UNSET || this._selected.size === 0) return;
    const ids = [...this._selected.keys()];
    this._bulkTitles = new Map(this._selected);
    this._bulkBusy = true;
    this._bulkError = undefined;
    this._bulkResult = undefined;
    const { data, error } = await tryExecute(this, postLibraryClipsBulk({ body: { action: this._bulkAction, ids, value: this._bulkValue } }));
    this._bulkBusy = false;
    this.#afterBulk(data, error);
  }

  async #deleteBulk() {
    if (this._selected.size === 0) return;
    const ids = [...this._selected.keys()];
    try {
      await umbConfirmModal(this, {
        headline: this.#t("bluebillywig_deleteVideosHeadline"),
        content: this.#t(ids.length === 1 ? "bluebillywig_deleteVideosOne" : "bluebillywig_deleteVideosMany", ids.length),
        color: "danger",
        confirmLabel: this.#t("bluebillywig_delete"),
      });
    } catch {
      return;
    }
    this._bulkTitles = new Map(this._selected);
    this._bulkBusy = true;
    this._bulkError = undefined;
    this._bulkResult = undefined;
    const { data, error } = await tryExecute(this, postLibraryClipsBulkDelete({ body: { ids } }));
    this._bulkBusy = false;
    this.#afterBulk(data, error);
  }

  #afterBulk(data: BulkResultResponseModel | undefined, error: unknown) {
    if (error || !data) {
      this._bulkError = saveError(error, "bulk action", this.#t);
      return;
    }
    this._bulkResult = data;
    // What worked leaves the selection; what failed stays selected so it can be tried again.
    const next = new Map(this._selected);
    data.succeeded.forEach((id) => next.delete(id));
    this._selected = next;
    void this.reload();
  }

  #renderBulk() {
    if (this.variant !== "videos") return nothing;
    const n = this._selected.size;
    const r = this._bulkResult;
    const options = this.#bulkOptions();
    return html`${n > 0
      ? html`<div class="bulk" id="bb-bulk" role="toolbar" aria-label=${this.#t("bluebillywig_bulkActions")}>
          <strong id="bb-bulk-count">${this.#t("bluebillywig_nSelected", n)}</strong>
          ${n >= MAX_BULK ? html`<span class="cap">${this.#t("bluebillywig_limitReached", MAX_BULK)}</span>` : nothing}
          <uui-select
            id="bb-bulk-action"
            label=${this.#t("bluebillywig_bulkAction")}
            .options=${[
              { name: this.#t("bluebillywig_bulkStatus"), value: "status", selected: this._bulkAction === "status" },
              { name: this.#t("bluebillywig_bulkFolder"), value: "folder", selected: this._bulkAction === "folder" },
              { name: this.#t("bluebillywig_bulkPolicy"), value: "cpp", selected: this._bulkAction === "cpp" },
            ]}
            @change=${(e: Event) => {
              this._bulkAction = (e.target as HTMLSelectElement).value as BulkAction;
              this._bulkValue = UNSET;
            }}></uui-select>
          <uui-select
            id="bb-bulk-value"
            label=${this.#t("bluebillywig_bulkValue")}
            .options=${[{ name: this.#t("bluebillywig_choose"), value: UNSET, selected: this._bulkValue === UNSET }, ...options.map((o) => ({ ...o, selected: o.value === this._bulkValue }))]}
            @change=${(e: Event) => (this._bulkValue = (e.target as HTMLSelectElement).value)}></uui-select>
          <uui-button id="bb-bulk-apply" look="primary" label=${this.#t("bluebillywig_apply")} ?disabled=${this._bulkBusy || this._bulkValue === UNSET} @click=${() => void this.#applyBulk()}>${this.#t("bluebillywig_apply")}</uui-button>
          ${this._canDelete
            ? html`<uui-button id="bb-bulk-delete" look="primary" color="danger" label=${this.#t("bluebillywig_deleteSelected")} ?disabled=${this._bulkBusy} @click=${() => void this.#deleteBulk()}>${this.#t("bluebillywig_deleteSelected")}</uui-button>`
            : nothing}
          <uui-button id="bb-bulk-clear" look="secondary" label=${this.#t("bluebillywig_clearSelection")} @click=${() => this.#clearSelection()}>${this.#t("bluebillywig_clearSelection")}</uui-button>
        </div>`
      : nothing}
    ${this._bulkError ? html`<p class="error-line" id="bb-bulk-error" role="alert">${this._bulkError}</p>` : nothing}
    ${r
      ? html`<div class="bulk-result" id="bb-bulk-result" role="status">
          <strong>${this.#t("bluebillywig_bulkResult", r.success, r.failed)}</strong>
          ${r.errors.length > 0
            ? html`<ul>${r.errors.map((e) => html`<li data-clip-id=${e.id}>${this._bulkTitles.get(e.id) ?? this.#t("bluebillywig_clipN", e.id)} (${e.id}): ${e.error}</li>`)}</ul>`
            : nothing}
        </div>`
      : nothing}`;
  }

  #renderFilters() {
    return html`<div class="filters">
      <uui-input
        id="bb-search"
        label=${this.#t("bluebillywig_search")}
        placeholder=${this.#t("bluebillywig_searchPlaceholder")}
        .value=${this._search}
        @input=${(e: Event) => {
          this._search = (e.target as HTMLInputElement).value;
          this.#refilter();
        }}></uui-input>
      ${this.#hasFilters
        ? html`<label class="inline">${this.#t("bluebillywig_from")}
            <uui-input
              id="bb-from"
              type="date"
              label=${this.#t("bluebillywig_publishedFrom")}
              .value=${this._from}
              @change=${(e: Event) => {
                this._from = (e.target as HTMLInputElement).value;
                this.#refilter();
              }}></uui-input></label>
          <label class="inline">${this.#t("bluebillywig_to")}
            <uui-input
              id="bb-to"
              type="date"
              label=${this.#t("bluebillywig_publishedTo")}
              .value=${this._to}
              @change=${(e: Event) => {
                this._to = (e.target as HTMLInputElement).value;
                this.#refilter();
              }}></uui-input></label>
          <uui-select
            id="bb-mediatype"
            label=${this.#t("bluebillywig_mediaType")}
            .options=${["all", "video", "audio", "image", "document"].map((v) => ({ name: this.#t("bluebillywig_media" + v[0].toUpperCase() + v.slice(1)), value: v, selected: v === this._mediaType }))}
            @change=${(e: Event) => {
              this._mediaType = (e.target as HTMLSelectElement).value;
              this.#refilter();
            }}></uui-select>`
        : nothing}
      <uui-button label=${this.#t("bluebillywig_clearFilters")} look="secondary" @click=${() => this.#clear()}>${this.#t("bluebillywig_clearFilters")}</uui-button>
      ${this.variant === "videos" && (this._result?.items.length ?? 0) > 0
        ? html`<uui-button id="bb-select-page" look="secondary" label=${this.#t("bluebillywig_selectPage")} @click=${() => this.#selectPage()}>${this.#t("bluebillywig_selectPage")}</uui-button>`
        : nothing}
      <span class="count" id="bb-count">${this._result ? this.#t("bluebillywig_nItems", this._result.total) : ""}</span>
    </div>`;
  }

  #renderPager() {
    const r = this._result;
    if (!r || r.total <= r.pageSize) return nothing;
    const pages = Math.ceil(r.total / r.pageSize);
    return html`<uui-pagination
      id="bb-pager"
      .total=${pages}
      .current=${this._page + 1}
      @change=${(e: Event) => {
        this._page = (e.target as HTMLElement & { current: number }).current - 1;
        void this.reload();
      }}></uui-pagination>`;
  }

  #renderBody() {
    if (this._error) {
      return html`<div class="error" id="bb-error" role="alert">
        <strong>${this.#t("bluebillywig_error")}</strong>
        <p>${this._error}</p>
        <uui-button label=${this.#t("bluebillywig_tryAgain")} look="primary" color="danger" @click=${() => void this.reload()}>${this.#t("bluebillywig_tryAgain")}</uui-button>
      </div>`;
    }
    if (this._loading && !this._result) return html`<uui-loader-bar></uui-loader-bar>`;
    const items = this._result?.items ?? [];
    if (items.length === 0) return html`<p class="empty" id="bb-empty">${this.#t(this.variant === "livestreams" ? "bluebillywig_noLivestreams" : "bluebillywig_noVideos")}</p>`;
    return html`${this._loading ? html`<uui-loader-bar></uui-loader-bar>` : nothing}
      <div class="grid" id="bb-grid">
        ${items.map(
          (clip) => html`<bb-clip-tile
            .clip=${clip}
            .selectable=${this.variant === "videos"}
            .selected=${this._selected.has(clip.id)}
            .selectionFull=${this._selected.size >= MAX_BULK}
            @toggle-select=${() => this.#toggle(clip)}
            @open-clip=${() => this.#open(clip)}></bb-clip-tile>`,
        )}
      </div>
      ${this.#renderPager()}`;
  }

  #renderRecentHeading() {
    const heading = listHeadingKey({ search: this._search, folder: this._folder, liveOnly: this._liveOnly, mediaType: this._mediaType, from: this._from, to: this._to });
    if (this.variant !== "picker" || !this._result) return nothing;
    return html`<h4 class="list-heading" id="bb-list-heading">${this.#t(heading)}</h4>`;
  }

  override render() {
    const showFolders = this.#hasFilters && this._folders.length > 0;
    const showSidebar = showFolders || this._hasLive;
    return html`${this.#renderFilters()} ${this.#renderBulk()}
      <div class="layout">
        ${showSidebar
          ? html`<div class="sidebar">
              ${this._hasLive
                ? html`<button
                    type="button"
                    id="bb-live-toggle"
                    class=${this._liveOnly ? "live active" : "live"}
                    aria-pressed=${this._liveOnly ? "true" : "false"}
                    @click=${() => {
                      ({ folder: this._folder, liveOnly: this._liveOnly } = selectLive(!this._liveOnly));
                      this._page = 0;
                      void this.reload();
                    }}>
                    <span aria-hidden="true">●</span> ${this.#t("bluebillywig_liveStreams")}
                  </button>`
                : nothing}
              ${showFolders
                ? html`<bb-folder-tree
                    .folders=${this._folders}
                    .active=${this._liveOnly ? "\u0000none" : this._folder}
                    @folder-change=${(e: CustomEvent<string>) => {
                      ({ folder: this._folder, liveOnly: this._liveOnly } = selectFolder(e.detail));
                      this._page = 0;
                      void this.reload();
                    }}></bb-folder-tree>`
                : nothing}
            </div>`
          : nothing}
        <div class="main">${this.#renderRecentHeading()} ${this.#renderBody()}</div>
      </div>`;
  }

  static override styles = css`
    :host { display: block; }
    .filters { display: flex; flex-wrap: wrap; align-items: center; gap: var(--uui-size-space-3); margin-bottom: var(--uui-size-space-4); }
    .filters #bb-search { flex: 1; min-width: 220px; }
    .inline { display: flex; align-items: center; gap: var(--uui-size-space-2); font-size: 12px; }
    .count { margin-left: auto; color: var(--uui-color-text-alt); }
    .bulk { display: flex; flex-wrap: wrap; align-items: center; gap: var(--uui-size-space-3); padding: var(--uui-size-space-3) var(--uui-size-space-4); margin-bottom: var(--uui-size-space-4); background: var(--uui-color-surface-emphasis); border-radius: var(--uui-border-radius); }
    .cap { color: var(--uui-color-text-alt); font-size: 12px; }
    .error-line { color: var(--uui-color-danger-standalone); }
    .bulk-result { margin-bottom: var(--uui-size-space-4); padding: var(--uui-size-space-3) var(--uui-size-space-4); border: 1px solid var(--uui-color-border); border-radius: var(--uui-border-radius); }
    .bulk-result ul { margin: var(--uui-size-space-2) 0 0; padding-left: 20px; color: var(--uui-color-danger-standalone); }
    .layout { display: flex; gap: var(--uui-size-space-5); align-items: flex-start; }
    bb-folder-tree { display: block; }
    .sidebar { width: 200px; min-width: 200px; max-height: 70vh; overflow: auto; }
    .live { display: block; width: 100%; text-align: left; margin-bottom: var(--uui-size-space-2); padding: 4px 8px; border: 0; border-radius: 3px; background: none; color: var(--uui-color-danger-standalone); font: inherit; font-weight: 600; cursor: pointer; }
    .live:hover { background: var(--uui-color-surface-emphasis); }
    .live.active { background: var(--uui-color-danger); color: var(--uui-color-danger-contrast); }
    .list-heading { margin: 0 0 var(--uui-size-space-3); font-size: 13px; color: var(--uui-color-text-alt); }
    .main { flex: 1; min-width: 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: var(--uui-size-space-4); }
    uui-pagination { display: block; margin-top: var(--uui-size-space-5); }
    .empty { color: var(--uui-color-text-alt); padding: var(--uui-size-space-5); }
    .error { border: 1px solid var(--uui-color-danger); border-radius: var(--uui-border-radius); padding: var(--uui-size-space-4); color: var(--uui-color-danger-standalone); background: var(--uui-color-surface); }
    .error p { margin: var(--uui-size-space-2) 0 var(--uui-size-space-3); }
  `;
}

export default BbClipBrowserElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-clip-browser": BbClipBrowserElement;
  }
}
