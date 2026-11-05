import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UMB_MODAL_MANAGER_CONTEXT, UmbModalBaseElement, umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import {
  deleteLibraryClipsById,
  getLibraryCapabilities,
  getLibraryClipsById,
  getLibraryClipsByIdEmbedCode,
  getLibraryClipsByIdPreview,
  type LibraryClipResponseModel,
} from "../api/index.js";
import { formatDate, formatDuration, libraryError, statusOf } from "./errors.js";
import { BB_CLIP_EDIT_MODAL, type ClipDetailsModalData, type ClipDetailsModalValue } from "./modal-tokens.js";

/**
 * Details of one clip: metadata, a preview, the embed code to copy and delete.
 * The preview is a sandboxed frame on the player host, so the player's JavaScript never runs in the backoffice.
 * Its URL comes from the server, which adds a short-lived token minted from the read-only key pair only.
 */
@customElement("bb-clip-details-modal")
export class BbClipDetailsModalElement extends UmbModalBaseElement<ClipDetailsModalData, ClipDetailsModalValue> {
  @state() private _clip?: LibraryClipResponseModel;
  @state() private _error?: string;
  @state() private _canDelete = false;
  @state() private _previewUrl?: string;
  @state() private _previewAuthenticated = false;
  @state() private _embedCode?: string;
  @state() private _message?: string;
  @state() private _busy = false;

  #changed = false;

  override async connectedCallback() {
    super.connectedCallback();
    await this.#load();
  }

  async #load() {
    const id = this.data?.clipId;
    if (!id) return;
    const [clip, caps] = await Promise.all([
      tryExecute(this, getLibraryClipsById({ path: { id } })),
      tryExecute(this, getLibraryCapabilities()),
    ]);
    if (clip.error || !clip.data) {
      this._error = statusOf(clip.error) === 404 ? "This clip no longer exists." : libraryError(clip.error, "clip");
      return;
    }
    this._clip = clip.data;
    this._canDelete = caps.data?.canDelete === true;
  }

  async #edit() {
    if (!this._clip) return;
    const modalManager = await this.getContext(UMB_MODAL_MANAGER_CONTEXT);
    if (!modalManager) return;
    const result = await modalManager.open(this, BB_CLIP_EDIT_MODAL, { data: { clipId: this._clip.id } }).onSubmit().catch(() => undefined);
    if (result?.changed) {
      this.#changed = true;
      // The preview and the embed code were made for the old state.
      this._previewUrl = undefined;
      this._embedCode = undefined;
      this._message = undefined;
      await this.#load();
    }
  }

  async #loadPreview() {
    if (!this._clip) return;
    const { data, error } = await tryExecute(this, getLibraryClipsByIdPreview({ path: { id: this._clip.id } }));
    if (error || !data) {
      this._error = libraryError(error, "preview");
      return;
    }
    this._previewUrl = data.url;
    this._previewAuthenticated = data.authenticated;
  }

  async #copyEmbed() {
    if (!this._clip) return;
    const { data, error } = await tryExecute(this, getLibraryClipsByIdEmbedCode({ path: { id: this._clip.id } }));
    if (error || !data) {
      this._error = libraryError(error, "embed code");
      return;
    }
    this._embedCode = data.code;
    try {
      await navigator.clipboard.writeText(data.code);
      this._message = "Embed code copied";
    } catch {
      this._message = "Select the embed code below and copy it";
    }
  }

  async #delete() {
    if (!this._clip) return;
    await umbConfirmModal(this, {
      headline: "Delete video",
      content: `Delete "${this._clip.title}" from Blue Billywig? This cannot be undone.`,
      color: "danger",
      confirmLabel: "Delete",
    });
    this._busy = true;
    const { error } = await tryExecute(this, deleteLibraryClipsById({ path: { id: this._clip.id } }));
    this._busy = false;
    if (error) {
      this._error = statusOf(error) === 403 ? "You do not have permission to delete videos." : libraryError(error, "video");
      return;
    }
    this.value = { deleted: true };
    this._submitModal();
  }

  override render() {
    const clip = this._clip;
    return html`<umb-body-layout headline=${clip?.title ?? "Video"}>
      ${this._error ? html`<uui-box><p class="error" id="bb-details-error" role="alert">${this._error}</p></uui-box>` : nothing}
      ${clip
        ? html`<uui-box>
              ${this._previewUrl
                ? html`<iframe
                      id="bb-preview-frame"
                      title="Preview"
                      src=${this._previewUrl}
                      sandbox="allow-scripts allow-same-origin allow-presentation allow-popups"
                      allow="autoplay; fullscreen"
                      referrerpolicy="no-referrer"></iframe>
                    <small>${this._previewAuthenticated ? "Preview uses a short-lived read-only token." : "Preview uses the public player: unpublished clips will not play. Set the read-only API pair in Settings to preview them."}</small>`
                : html`<div class="poster">
                    ${clip.thumbnailUrl ? html`<img src=${clip.thumbnailUrl} alt="" />` : html`<uui-icon name="icon-movie-alt"></uui-icon>`}
                    <uui-button id="bb-load-preview" label="Load preview" look="primary" @click=${() => void this.#loadPreview()}>Load preview</uui-button>
                  </div>`}
            </uui-box>
            <uui-box headline="Details">
              <dl>
                <dt>ID</dt><dd>${clip.id}</dd>
                <dt>Status</dt><dd>${clip.status ?? ""}</dd>
                <dt>Type</dt><dd>${clip.mediaType ?? ""}${clip.sourceType ? ` (${clip.sourceType})` : ""}</dd>
                <dt>Length</dt><dd>${formatDuration(clip.length)}</dd>
                <dt>Created</dt><dd>${formatDate(clip.createdDate)}</dd>
                <dt>Updated</dt><dd>${formatDate(clip.updatedDate)}</dd>
                <dt>Tags</dt><dd>${clip.tags.join(", ")}</dd>
                <dt>Description</dt><dd class="description">${clip.description}</dd>
              </dl>
            </uui-box>
            <uui-box headline="Embed">
              <uui-button id="bb-copy-embed" label="Copy embed code" look="secondary" @click=${() => void this.#copyEmbed()}>Copy embed code</uui-button>
              ${this._message ? html`<span class="ok" id="bb-embed-message">${this._message}</span>` : nothing}
              ${this._embedCode ? html`<uui-textarea id="bb-embed-code" label="Embed code" readonly .value=${this._embedCode}></uui-textarea>` : nothing}
            </uui-box>`
        : this._error
          ? nothing
          : html`<uui-loader-bar></uui-loader-bar>`}
      <div slot="actions">
        ${clip ? html`<uui-button id="bb-edit" label="Edit" look="primary" @click=${() => void this.#edit()}>Edit</uui-button>` : nothing}
        ${this._canDelete && clip
          ? html`<uui-button id="bb-delete" label="Delete" look="primary" color="danger" ?disabled=${this._busy} @click=${() => void this.#delete()}>Delete</uui-button>`
          : nothing}
        <uui-button label="Close" @click=${() => { this.value = { changed: this.#changed }; this._submitModal(); }}>Close</uui-button>
      </div>
    </umb-body-layout>`;
  }

  static override styles = css`
    uui-box { margin-bottom: var(--uui-size-space-4); }
    iframe { width: 100%; aspect-ratio: 16 / 9; border: 0; background: #000; }
    .poster { position: relative; aspect-ratio: 16 / 9; background: #000; display: flex; align-items: center; justify-content: center; }
    .poster img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0.7; }
    .poster uui-button { position: relative; }
    dl { display: grid; grid-template-columns: 110px 1fr; gap: var(--uui-size-space-2) var(--uui-size-space-4); margin: 0; }
    dt { color: var(--uui-color-text-alt); }
    dd { margin: 0; overflow-wrap: anywhere; }
    .description { white-space: pre-wrap; }
    .error { color: var(--uui-color-danger-standalone); margin: 0; }
    .ok { margin-left: var(--uui-size-space-3); color: var(--uui-color-positive-standalone); }
    uui-textarea { width: 100%; margin-top: var(--uui-size-space-3); }
  `;
}

export default BbClipDetailsModalElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-clip-details-modal": BbClipDetailsModalElement;
  }
}
