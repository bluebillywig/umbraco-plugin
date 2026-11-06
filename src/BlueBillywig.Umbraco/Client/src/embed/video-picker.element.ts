import { css, customElement, html, LitElement, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { UMB_MODAL_MANAGER_CONTEXT } from "@umbraco-cms/backoffice/modal";
import { BB_LIBRARY_PICKER_MODAL } from "../library/modal-tokens.js";
import type { UmbPropertyEditorUiElement } from "@umbraco-cms/backoffice/property-editor";
import {
  getChannels,
  getClips,
  getClipsById,
  getFeatures,
  getLibraryCapabilities,
  getPlayouts,
  getPreview,
  getShorts,
  putLibraryClipsById,
  type ClipSummaryResponseModel,
  type EntityOptionResponseModel,
  type PlayoutOptionResponseModel,
} from "../api/index.js";
import { defaultsFor, readValue, selectionChanged, type VideoType, type VideoValue } from "./video-value.js";
import { actionRequest, actionTook, applySaved, clipBanner, embargoText, type BannerAction } from "./clip-status.js";
import { offeredTypes, readFeatures, type EmbedFeatures } from "./features.js";
import { PREVIEW_ALLOW, PREVIEW_REFERRER_POLICY, PREVIEW_SANDBOX, previewHeight, previewKey, previewQuery } from "./preview.js";
import { libraryError, saveError } from "../library/errors.js";
import type { Term } from "../localization/terms.js";
import "../shared/credentials-banner.element.js";

const TYPES: Array<{ value: VideoType; label: string }> = [
  { value: "clip", label: "bluebillywig_typeClip" },
  { value: "channel", label: "bluebillywig_typeChannel" },
  { value: "shorts", label: "bluebillywig_typeShorts" },
];

@customElement("bb-video-picker")
export class BbVideoPickerElement extends UmbElementMixin(LitElement) implements UmbPropertyEditorUiElement {
  #t: Term = (key, ...args) => this.localize.term(key, ...args);
  #searchTimer?: ReturnType<typeof setTimeout>;
  #searchSeq = 0;

  @property({ attribute: false })
  set value(raw: unknown) {
    const parsed = readValue(raw);
    const changedClip = parsed?.type === "clip" && parsed.id !== this._value?.id;
    if (selectionChanged(this._value, parsed)) this._bannerMessage = this._bannerError = undefined;
    this._value = parsed;
    if (changedClip && parsed) void this.#loadSelectedClip(parsed.id);
    if (!parsed) this._selectedClip = undefined;
  }
  get value(): VideoValue | undefined {
    return this._value;
  }

  @state() private _value?: VideoValue;
  @state() private _type: VideoType = "clip";
  @state() private _query = "";
  @state() private _clips: ClipSummaryResponseModel[] = [];
  @state() private _selectedClip?: ClipSummaryResponseModel;
  @state() private _entities: EntityOptionResponseModel[] = [];
  @state() private _playouts: PlayoutOptionResponseModel[] = [];
  @state() private _loading = false;
  // A failed list, search or playout call. Never shown as "No clips found": the reference plugin's picker also keeps the
  // two apart (a fetch error is not an empty result), and a retry runs the call that failed.
  @state() private _error?: string;
  @state() private _retry: "playouts" | "list" = "list";
  @state() private _showResults = false;
  // Which types the publication has (undefined until known, and when the call failed: then every type is offered).
  @state() private _features?: EmbedFeatures;
  // The loaded preview belongs to one key (type, id, playout, publication); a different key shows the button again.
  @state() private _preview?: { key: string; url: string; authenticated: boolean };
  @state() private _previewError?: string;
  @state() private _previewBusy = false;
  // The status banner's actions need the Blue Billywig section (they edit the clip); undefined until asked.
  @state() private _canEditClip?: boolean;
  @state() private _bannerBusy = false;
  @state() private _bannerMessage?: string;
  @state() private _bannerError?: string;

  override connectedCallback() {
    super.connectedCallback();
    if (this._value) this._type = this._value.type;
    void this.#loadPlayouts();
    void this.#loadForType();
    void this.#loadFeatures();
  }

  /** Channel and shorts are offered only when the publication has some. A failed call changes nothing: every type stays offered. */
  async #loadFeatures() {
    const { data, error } = await tryExecute(this, getFeatures(), { disableNotifications: true });
    this._features = readFeatures(data, error);
  }

  async #loadPreview() {
    const v = this._value;
    if (!v) return;
    const key = previewKey(v);
    this._previewBusy = true;
    this._previewError = undefined;
    const { data, error } = await tryExecute(this, getPreview({ query: previewQuery(v) }), { disableNotifications: true });
    this._previewBusy = false;
    // The value may have changed while the call was out: a URL for another value is never shown.
    if (previewKey(this._value) !== key) return;
    if (error || !data) this._previewError = libraryError(error, "preview", this.#t);
    else this._preview = { key, url: data.url, authenticated: data.authenticated };
  }

  async #banner(action: BannerAction) {
    const clip = this._selectedClip;
    if (!clip) return;
    this._bannerBusy = true;
    this._bannerError = undefined;
    this._bannerMessage = undefined;
    const { data, error } = await tryExecute(this, putLibraryClipsById({ path: { id: clip.id }, body: actionRequest(action) }), { disableNotifications: true });
    this._bannerBusy = false;
    if (error) {
      this._bannerError = saveError(error, "clip status", this.#t);
      return;
    }
    // The server answers with the clip read back from Blue Billywig. Success is shown only when that clip shows the change.
    if (!data || !actionTook(action, data)) {
      this._bannerError = saveError(undefined, "clip status", this.#t);
      return;
    }
    if (this._selectedClip?.id === clip.id) this._selectedClip = applySaved(this._selectedClip, action, data);
    this._bannerMessage = this.#t(action === "publish" ? "bluebillywig_clipPublished" : "bluebillywig_embargoRemoved");
  }

  override updated(changed: Map<PropertyKey, unknown>) {
    super.updated(changed);
    // Ask once, when the first banner shows, whether this user may edit clips (the buttons are hidden for those who may not).
    if (changed.has("_selectedClip") && this._value?.type === "clip" && clipBanner(this._selectedClip)) void this.#askCanEditClip();
  }

  async #askCanEditClip() {
    if (this._canEditClip !== undefined) return;
    this._canEditClip = false;
    const { error } = await tryExecute(this, getLibraryCapabilities(), { disableNotifications: true });
    this._canEditClip = !error;
  }

  async #loadPlayouts() {
    const { data, error } = await tryExecute(this, getPlayouts(), { disableNotifications: true });
    if (error || !data) {
      this._error = libraryError(error, "playouts", this.#t);
      this._retry = "playouts";
    } else {
      this._playouts = data;
      if (this._retry === "playouts") this._error = undefined;
    }
  }

  async #loadSelectedClip(id: number) {
    const { data } = await tryExecute(this, getClipsById({ path: { id } }));
    if (data && this._value?.id === id) this._selectedClip = data;
  }

  /** Channels and shorts are short lists, so they load once; clips load on search. */
  async #loadForType() {
    this._error = undefined;
    if (this._type === "clip") {
      this.#search();
      return;
    }
    this._loading = true;
    const { data, error } = await tryExecute(this, this._type === "channel" ? getChannels() : getShorts(), { disableNotifications: true });
    this._loading = false;
    if (error || !data) {
      this._error = libraryError(error, this._type === "channel" ? "channels" : "shorts", this.#t);
      this._retry = "list";
    } else this._entities = data;
  }

  #search() {
    clearTimeout(this.#searchTimer);
    const seq = ++this.#searchSeq;
    this.#searchTimer = setTimeout(async () => {
      this._loading = true;
      this._error = undefined;
      const { data, error } = await tryExecute(this, getClips({ query: this._query ? { search: this._query } : {} }), { disableNotifications: true });
      if (seq !== this.#searchSeq) return;
      this._loading = false;
      if (error || !data) {
        // Keep nothing from an earlier answer: a failure must not look like results.
        this._clips = [];
        this._error = libraryError(error, "clips", this.#t);
        this._retry = "list";
      } else this._clips = data.items;
    }, 300);
  }

  async #retryFailed() {
    if (this._retry === "playouts") {
      this._error = undefined;
      await this.#loadPlayouts();
    } else await this.#loadForType();
  }

  #commit(value: VideoValue | undefined) {
    // "Embargo removed." and the like describe the clip that was selected: gone with it, or replaced by another.
    if (selectionChanged(this._value, value)) this._bannerMessage = this._bannerError = undefined;
    this._value = value;
    this.dispatchEvent(new UmbChangeEvent());
  }

  #setType(type: VideoType) {
    if (type === this._type) return;
    this._type = type;
    this._entities = [];
    this._selectedClip = undefined;
    this._bannerMessage = this._bannerError = undefined;
    this._showResults = false;
    // A different type invalidates the id: ids of clips, channels and shorts are unrelated.
    this.#commit(undefined);
    void this.#loadForType();
  }

  #select(id: number, clip?: ClipSummaryResponseModel) {
    this._selectedClip = clip;
    this._bannerMessage = this._bannerError = undefined;
    // The picker rows carry no embargo window, so the banner reads the clip itself.
    if (this._type === "clip") void this.#loadSelectedClip(id);
    this._showResults = false;
    this.#commit(this._value && this._value.type === this._type ? { ...this._value, id } : defaultsFor(this._type, id));
  }

  #patch(patch: Partial<VideoValue>) {
    if (this._value) this.#commit({ ...this._value, ...patch });
  }

  #toggle(field: "showTranscript" | "showChapters" | "lazyLoad" | "fullscreen", label: string) {
    return html`<label class="toggle">
      <uui-toggle
        .checked=${Boolean(this._value?.[field])}
        label=${label}
        @change=${(e: Event) => this.#patch({ [field]: (e.target as HTMLInputElement).checked })}></uui-toggle>
    </label>`;
  }

  #thumb(url?: string | null) {
    return url ? html`<img class="thumb" src=${url} alt="" loading="lazy" />` : html`<span class="thumb placeholder"><uui-icon name="icon-movie-alt"></uui-icon></span>`;
  }

  #renderSelected() {
    const v = this._value;
    if (!v) return html`<p class="hint">${this.#t("bluebillywig_nothingSelected")}</p>`;
    if (v.type === "clip") {
      const clip = this._selectedClip;
      return html`<div class="selected">
        ${this.#thumb(clip?.thumbnailUrl)}
        <div class="meta"><strong>${clip?.title ?? this.#t("bluebillywig_clipN", v.id)}</strong><small>${this.#t("bluebillywig_clipN", v.id)}</small></div>
        <uui-button label=${this.#t("bluebillywig_remove")} look="secondary" compact @click=${() => { this._selectedClip = undefined; this.#commit(undefined); }}>${this.#t("bluebillywig_remove")}</uui-button>
      </div>`;
    }
    const entity = this._entities.find((e) => e.id === v.id);
    return html`<div class="selected">
      ${this.#thumb()}
      <div class="meta"><strong>${entity?.name ?? this.#t(v.type === "channel" ? "bluebillywig_channelN" : "bluebillywig_shortsN", v.id)}</strong><small>${v.type} ${v.id}</small></div>
      <uui-button label=${this.#t("bluebillywig_remove")} look="secondary" compact @click=${() => this.#commit(undefined)}>${this.#t("bluebillywig_remove")}</uui-button>
    </div>`;
  }

  /** Draft, archived or embargoed: say so, with the actions that fix it (the reference plugin's banner above the block). */
  #renderBanner() {
    const banner = this._value?.type === "clip" ? clipBanner(this._selectedClip) : undefined;
    if (!banner) return this._bannerMessage ? html`<p class="banner-message" id="bb-banner-message" role="status">${this._bannerMessage}</p>` : nothing;
    const text =
      banner.kind === "draft"
        ? this.#t("bluebillywig_bannerDraft")
        : banner.kind === "archived"
          ? this.#t("bluebillywig_bannerArchived")
          : this.#t("bluebillywig_bannerEmbargo", embargoText(banner.embargo?.from ?? ""), embargoText(banner.embargo?.to ?? ""));
    return html`<div class=${`banner ${banner.kind}`} id="bb-status-banner" role="status">
        <span>${text}</span>
        ${this._canEditClip
          ? html`<span class="banner-actions">
              ${banner.actions.map(
                (a) => html`<uui-button
                  look="primary"
                  compact
                  data-action=${a}
                  label=${this.#t(a === "publish" ? "bluebillywig_publishNow" : "bluebillywig_removeEmbargo")}
                  ?disabled=${this._bannerBusy}
                  @click=${() => void this.#banner(a)}>${this.#t(a === "publish" ? "bluebillywig_publishNow" : "bluebillywig_removeEmbargo")}</uui-button>`,
              )}
            </span>`
          : nothing}
      </div>
      ${this._bannerError ? html`<p class="error-line" id="bb-banner-error" role="alert">${this._bannerError}</p>` : nothing}`;
  }

  /**
   * The live player in the editor, behind a button: nothing loads from the player host until the editor asks (the same
   * pattern as the library details). The URL comes from the server with a read-only session token, so drafts render.
   */
  #renderPreview() {
    const v = this._value;
    if (!v) return nothing;
    const loaded = this._preview?.key === previewKey(v) ? this._preview : undefined;
    const height = previewHeight(v);
    return html`<div class="preview">
      ${loaded
        ? html`<iframe
              id="bb-embed-preview-frame"
              class=${height ? "fixed" : "ratio"}
              style=${height ? `height:${height}` : ""}
              title=${this.#t("bluebillywig_previewTitle")}
              src=${loaded.url}
              sandbox=${PREVIEW_SANDBOX}
              allow=${PREVIEW_ALLOW}
              referrerpolicy=${PREVIEW_REFERRER_POLICY}></iframe>
            <small>${this.#t(loaded.authenticated ? "bluebillywig_previewAuthenticated" : "bluebillywig_previewPublic")}</small>`
        : html`<uui-button
            id="bb-embed-load-preview"
            look="secondary"
            label=${this.#t("bluebillywig_loadPreview")}
            ?disabled=${this._previewBusy}
            @click=${() => void this.#loadPreview()}>${this.#t("bluebillywig_loadPreview")}</uui-button>`}
      ${this._previewError ? html`<p class="error-line" id="bb-embed-preview-error" role="alert">${this._previewError}</p>` : nothing}
    </div>`;
  }

  /** Opens the library browser as a modal (parity E5): paging, filters and folders, like the reference plugin's "Blue Billywig" media tab. */
  async #browse() {
    const modalManager = await this.getContext(UMB_MODAL_MANAGER_CONTEXT);
    if (!modalManager) return;
    const picked = await modalManager.open(this, BB_LIBRARY_PICKER_MODAL, { data: {} }).onSubmit().catch(() => undefined);
    if (picked) this.#select(picked.id, { id: picked.id, title: picked.title, thumbnailUrl: picked.thumbnailUrl ?? null, length: picked.length ?? null, status: picked.status ?? null });
  }

  #renderClipSearch() {
    return html`<uui-button id="bb-browse" look="secondary" label=${this.#t("bluebillywig_browseLibrary")} @click=${() => void this.#browse()}>
        <uui-icon name="icon-search"></uui-icon> ${this.#t("bluebillywig_browseLibrary")}
      </uui-button>
      <uui-input
        id="bb-search"
        label=${this.#t("bluebillywig_searchClips")}
        placeholder=${this.#t("bluebillywig_searchLibraryPlaceholder")}
        .value=${this._query}
        @focus=${() => (this._showResults = true)}
        @input=${(e: Event) => {
          this._query = (e.target as HTMLInputElement).value;
          this._showResults = true;
          this.#search();
        }}></uui-input>
      ${this._showResults
        ? html`<ul class="results" id="bb-results">
            ${this._loading ? html`<li class="hint"><uui-loader-bar></uui-loader-bar></li>` : nothing}
            ${!this._loading && this._clips.length === 0 && !this._error ? html`<li class="hint">${this.#t("bluebillywig_noClips")}</li>` : nothing}
            ${this._clips.map(
              (clip) => html`<li>
                <button type="button" class="result" data-clip-id=${clip.id} @click=${() => this.#select(clip.id, clip)}>
                  ${this.#thumb(clip.thumbnailUrl)}
                  <span class="meta"><strong>${clip.title}</strong><small>${clip.id}${clip.status ? ` · ${clip.status}` : ""}</small></span>
                </button>
              </li>`,
            )}
          </ul>`
        : nothing}`;
  }

  #renderEntityPicker() {
    return html`<uui-select
      label=${this.#t(this._type === "channel" ? "bluebillywig_typeChannel" : "bluebillywig_typeShorts")}
      placeholder=${this.#t(this._loading ? "bluebillywig_loading" : this._type === "channel" ? "bluebillywig_chooseChannel" : "bluebillywig_chooseShorts")}
      .options=${this._entities.map((e) => ({ name: e.name, value: String(e.id), selected: e.id === this._value?.id }))}
      @change=${(e: Event) => {
        const id = Number((e.target as HTMLSelectElement).value);
        if (id > 0) this.#select(id);
      }}></uui-select>`;
  }

  #renderOptions() {
    const v = this._value;
    if (!v) return nothing;
    if (v.type === "clip") {
      return html`<div class="options">
        <label>${this.#t("bluebillywig_playout")}
          <uui-select
            label=${this.#t("bluebillywig_playout")}
            .options=${[
              { name: this.#t("bluebillywig_siteDefault"), value: "", selected: !v.playout },
              ...this._playouts.map((p) => ({ name: p.label, value: p.value, selected: p.value === v.playout })),
            ]}
            @change=${(e: Event) => this.#patch({ playout: (e.target as HTMLSelectElement).value || undefined })}></uui-select>
        </label>
        ${this.#toggle("showTranscript", this.#t("bluebillywig_showTranscript"))} ${this.#toggle("showChapters", this.#t("bluebillywig_showChapters"))} ${this.#toggle("lazyLoad", this.#t("bluebillywig_lazyLoad"))}
      </div>`;
    }
    return html`<div class="options">
      <label>${this.#t("bluebillywig_height")}
        <uui-input label=${this.#t("bluebillywig_height")} .value=${v.height ?? ""} placeholder="600px" @change=${(e: Event) => this.#patch({ height: (e.target as HTMLInputElement).value.trim() })}></uui-input>
      </label>
      ${v.type === "channel" ? this.#toggle("lazyLoad", this.#t("bluebillywig_lazyLoad")) : this.#toggle("fullscreen", this.#t("bluebillywig_fullscreen"))}
    </div>`;
  }

  override render() {
    const offered = offeredTypes(TYPES.map((t) => t.value), this._features, this._value?.type);
    return html`
      <bb-credentials-banner></bb-credentials-banner>
      <uui-button-group>
        ${TYPES.filter((t) => offered.includes(t.value)).map(
          (t) => html`<uui-button
            look=${this._type === t.value ? "primary" : "outline"}
            label=${this.#t(t.label)}
            data-type=${t.value}
            @click=${() => this.#setType(t.value)}></uui-button>`,
        )}
      </uui-button-group>
      ${this._error
        ? html`<div class="error" id="bb-picker-error" role="alert">
            <span>${this._error}</span>
            <uui-button id="bb-picker-retry" label=${this.#t("bluebillywig_tryAgain")} look="secondary" compact @click=${() => void this.#retryFailed()}>${this.#t("bluebillywig_tryAgain")}</uui-button>
          </div>`
        : nothing}
      <div class="picker">${this._type === "clip" ? this.#renderClipSearch() : this.#renderEntityPicker()}</div>
      ${this.#renderBanner()} ${this.#renderSelected()} ${this.#renderPreview()} ${this.#renderOptions()}
    `;
  }

  static override styles = css`
    :host { display: block; }
    .picker, .selected, .options { margin-top: var(--uui-size-space-4); }
    #bb-browse { margin-bottom: var(--uui-size-space-3); }
    uui-input, uui-select { width: 100%; }
    .results { list-style: none; margin: var(--uui-size-space-2) 0 0; padding: 0; max-height: 320px; overflow: auto; border: 1px solid var(--uui-color-border); border-radius: var(--uui-border-radius); background: var(--uui-color-surface); }
    .result { display: flex; align-items: center; gap: var(--uui-size-space-3); width: 100%; padding: var(--uui-size-space-2) var(--uui-size-space-3); border: 0; background: none; text-align: left; cursor: pointer; font: inherit; color: inherit; }
    .result:hover, .result:focus-visible { background: var(--uui-color-surface-emphasis); }
    .selected { display: flex; align-items: center; gap: var(--uui-size-space-3); padding: var(--uui-size-space-3); border: 1px solid var(--uui-color-border); border-radius: var(--uui-border-radius); }
    .meta { display: flex; flex-direction: column; flex: 1; min-width: 0; }
    .meta strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    small, .hint { color: var(--uui-color-text-alt); }
    .hint { padding: var(--uui-size-space-3); margin: 0; }
    .thumb { width: 96px; height: 54px; object-fit: cover; border-radius: 3px; background: #000; flex: none; }
    .thumb.placeholder { display: inline-flex; align-items: center; justify-content: center; background: var(--uui-color-surface-alt); color: var(--uui-color-text-alt); }
    .options { display: grid; gap: var(--uui-size-space-4); }
    .toggle { display: flex; align-items: center; gap: var(--uui-size-space-3); }
    label { display: grid; gap: var(--uui-size-space-1); }
    .banner { display: flex; align-items: center; justify-content: space-between; gap: var(--uui-size-space-3); margin-top: var(--uui-size-space-4); padding: var(--uui-size-space-3) var(--uui-size-space-4); border-radius: var(--uui-border-radius); border: 1px solid var(--uui-color-border); }
    .banner.draft { background: var(--uui-color-warning); color: var(--uui-color-warning-contrast); }
    .banner.archived { background: var(--uui-color-danger); color: var(--uui-color-danger-contrast); }
    .banner.embargo { background: var(--uui-color-default); color: var(--uui-color-default-contrast); }
    .banner-actions { display: flex; gap: var(--uui-size-space-2); }
    .banner-message { margin: var(--uui-size-space-3) 0 0; color: var(--uui-color-text-alt); }
    .error-line { color: var(--uui-color-danger-standalone); margin: var(--uui-size-space-2) 0 0; }
    .preview { margin-top: var(--uui-size-space-4); }
    .preview iframe { display: block; width: 100%; border: 0; border-radius: var(--uui-border-radius); background: #000; }
    .preview iframe.ratio { aspect-ratio: 16 / 9; }
    .error { display: flex; align-items: center; gap: var(--uui-size-space-3); margin-top: var(--uui-size-space-3); color: var(--uui-color-danger-standalone); }
  `;
}

export default BbVideoPickerElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-video-picker": BbVideoPickerElement;
  }
}
