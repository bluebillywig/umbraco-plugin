import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement, umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import {
  deleteLibraryClipsByIdSubtitlesBySubtitleId,
  getLibraryCapabilities,
  getLibraryClipsByIdEdit,
  getLibraryCustomFieldGroups,
  getLibraryFolders,
  getLibraryLanguages,
  getLibraryPlayouts,
  getLibraryPolicies,
  postLibraryClipsByIdSubtitles,
  postLibraryClipsByIdSubtitlesBySubtitleIdDefault,
  postLibraryClipsByIdThumbnails,
  postLibraryClipsByIdThumbnailsMain,
  putLibraryClipsById,
  type ClipEditResponseModel,
  type ClipUpdateRequestModel,
  type CustomFieldGroupResponseModel,
  type FolderResponseModel,
  type LanguageResponseModel,
  type PlayoutOptionResponseModel,
  type PolicyResponseModel,
  type SubtitleResponseModel,
  type ThumbnailResponseModel,
} from "../api/index.js";
import { saveError, statusOf } from "./errors.js";
import type { ClipEditModalData, ClipEditModalValue } from "./modal-tokens.js";
import "./tag-chips.element.js";
import "./clip-analytics.element.js";
import "./cta-editor.element.js";

const STATUSES = ["published", "draft", "archived"];
const MAX_THUMBNAIL_BYTES = 10 * 1024 * 1024;
const MAX_SUBTITLE_BYTES = 1024 * 1024;

type Option = { name: string; value: string; selected?: boolean };

/** `2030-01-01T10:00:00Z` to the value of a datetime-local input. The dates are UTC, which the label says. */
function toInput(iso?: string | null): string {
  if (!iso) return "";
  return iso.length === 10 ? `${iso}T00:00` : iso.slice(0, 16).replace(" ", "T");
}

function fromInput(value: string): string {
  return value ? `${value}:00Z` : "";
}

function flatten(folders: FolderResponseModel[], depth = 0): Option[] {
  return folders.flatMap((f) => [{ name: `${"  ".repeat(depth)}${f.name}`, value: f.code }, ...flatten(f.children, depth + 1)]);
}

/**
 * The clip editor: title, description, tags, status, content protection, playout, folder, language and embargo dates,
 * custom fields, thumbnails and subtitles. Port of the reference plugin's edit drawer (bb-edit-drawer.js), as a sidebar modal, which is
 * how Umbraco shows an editor for something that is not an entity of its own.
 *
 * Only the fields that changed are sent, so a tag edit cannot blank the title. Each save is read back from the server and
 * the form shows what Blue Billywig really holds. A refused or failed save is shown and nothing is reported as saved.
 * Thumbnail and subtitle changes are saved the moment they are made, like in the drawer.
 */
@customElement("bb-clip-edit-modal")
export class BbClipEditModalElement extends UmbModalBaseElement<ClipEditModalData, ClipEditModalValue> {
  @state() private _clip?: ClipEditResponseModel;
  @state() private _loadError?: string;
  @state() private _error?: string;
  @state() private _message?: string;
  @state() private _saving = false;
  @state() private _canDelete = false;

  @state() private _title = "";
  @state() private _description = "";
  @state() private _tags: string[] = [];
  @state() private _status = "published";
  @state() private _cpp = "";
  @state() private _folder = "";
  @state() private _playout = "";
  @state() private _language = "";
  @state() private _embargoFrom = "";
  @state() private _embargoTo = "";
  @state() private _custom: Record<string, string> = {};

  @state() private _policies: PolicyResponseModel[] = [];
  @state() private _playouts: PlayoutOptionResponseModel[] = [];
  @state() private _folders: FolderResponseModel[] = [];
  @state() private _languages: LanguageResponseModel[] = [];
  @state() private _groups: CustomFieldGroupResponseModel[] = [];

  @state() private _thumbs: ThumbnailResponseModel[] = [];
  @state() private _thumbStatus?: string;
  @state() private _thumbBusy = false;

  @state() private _subs: SubtitleResponseModel[] = [];
  @state() private _subStatus?: string;
  @state() private _subError?: string;
  @state() private _subBusy = false;
  @state() private _subIso = "";
  @state() private _subName = "";
  @state() private _subPublish = false;
  @state() private _subDefault = false;
  @state() private _subFile?: File;

  #changed = false;

  override async connectedCallback() {
    super.connectedCallback();
    const id = this.data?.clipId;
    if (!id) return;
    const [clip, caps] = await Promise.all([tryExecute(this, getLibraryClipsByIdEdit({ path: { id } }), { disableNotifications: true }), tryExecute(this, getLibraryCapabilities(), { disableNotifications: true })]);
    if (clip.error || !clip.data) {
      this._loadError = saveError(clip.error, "clip");
      return;
    }
    this._canDelete = caps.data?.canDelete === true;
    this.#fill(clip.data);
    void this.#loadLookups(clip.data);
  }

  #fill(clip: ClipEditResponseModel) {
    this._clip = clip;
    this._title = clip.title;
    this._description = clip.description;
    this._tags = [...clip.tags];
    this._status = clip.status ?? "published";
    this._cpp = clip.cpp ?? "";
    this._folder = clip.folderCode ?? "";
    this._playout = clip.playout ?? "";
    this._language = clip.language ?? "";
    this._embargoFrom = toInput(clip.embargoFrom);
    this._embargoTo = toInput(clip.embargoTo);
    this._custom = { ...clip.customFields };
    this._thumbs = clip.thumbnails;
    this._subs = clip.subtitles;
  }

  async #loadLookups(clip: ClipEditResponseModel) {
    // A lookup that fails leaves its dropdown with only the current value. The form still works.
    const [policies, playouts, folders, languages, groups] = await Promise.all([
      tryExecute(this, getLibraryPolicies()),
      tryExecute(this, getLibraryPlayouts()),
      tryExecute(this, getLibraryFolders()),
      tryExecute(this, getLibraryLanguages()),
      clip.customFieldsEnabled ? tryExecute(this, getLibraryCustomFieldGroups()) : Promise.resolve({ data: [] as CustomFieldGroupResponseModel[], error: undefined }),
    ]);
    this._policies = policies.data ?? [];
    this._playouts = playouts.data ?? [];
    this._folders = folders.data ?? [];
    this._languages = languages.data ?? [];
    this._groups = groups.data ?? [];
  }

  /** The fields that differ from what the server last returned. */
  #diff(): ClipUpdateRequestModel {
    const c = this._clip;
    const body: ClipUpdateRequestModel = {};
    if (!c) return body;
    if (this._title !== c.title) body.title = this._title;
    if (this._description !== c.description) body.description = this._description;
    if (this._tags.join("\u0000") !== c.tags.join("\u0000")) body.tags = this._tags;
    if (this._status !== (c.status ?? "published")) body.status = this._status;
    if (this._cpp !== (c.cpp ?? "")) body.cpp = this._cpp;
    if (this._folder !== (c.folderCode ?? "")) body.folderCode = this._folder;
    if (this._playout !== (c.playout ?? "")) body.playout = this._playout;
    // A language cannot be cleared in Blue Billywig, so only a real choice is sent.
    if (this._language !== (c.language ?? "") && this._language) body.language = this._language;
    if (this._embargoFrom !== toInput(c.embargoFrom)) body.embargoFrom = fromInput(this._embargoFrom);
    if (this._embargoTo !== toInput(c.embargoTo)) body.embargoTo = fromInput(this._embargoTo);
    const custom: Record<string, string> = {};
    for (const [name, value] of Object.entries(this._custom)) {
      if (value !== (c.customFields[name] ?? "")) custom[name] = value;
    }
    if (Object.keys(custom).length > 0) body.customFields = custom;
    return body;
  }

  get #dirty() {
    return Object.keys(this.#diff()).length > 0;
  }

  async #save() {
    const id = this._clip?.id;
    if (!id) return;
    const body = this.#diff();
    this._error = undefined;
    this._message = undefined;
    if (Object.keys(body).length === 0) {
      this._message = "No changes to save.";
      return;
    }
    this._saving = true;
    const { data, error } = await tryExecute(this, putLibraryClipsById({ path: { id }, body }), { disableNotifications: true });
    this._saving = false;
    if (error || !data) {
      this._error = saveError(error, "changes");
      return;
    }
    this.#changed = true;
    this.#fill(data);
    this._message = "Saved.";
  }

  // ---- thumbnails -------------------------------------------------------------------------------------------------

  async #setMain(thumb: ThumbnailResponseModel) {
    const id = this._clip?.id;
    if (!id || !thumb.src || thumb.main) return;
    this._thumbBusy = true;
    this._thumbStatus = "Setting…";
    const { data, error } = await tryExecute(this, postLibraryClipsByIdThumbnailsMain({ path: { id }, body: { src: thumb.src } }), { disableNotifications: true });
    this._thumbBusy = false;
    if (error || !data) {
      this._thumbStatus = saveError(error, "thumbnail change");
      // The list was outdated: show the current one so the editor can pick again.
      if (statusOf(error) === 409) await this.#reloadThumbs();
      return;
    }
    this.#changed = true;
    this._thumbs = data.thumbnails;
    this._thumbStatus = data.notice ?? "Main thumbnail set.";
  }

  async #reloadThumbs() {
    const id = this._clip?.id;
    if (!id) return;
    const { data } = await tryExecute(this, getLibraryClipsByIdEdit({ path: { id } }));
    if (data) this._thumbs = data.thumbnails;
  }

  async #uploadThumbnail(e: Event) {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = "";
    const id = this._clip?.id;
    if (!file || !id) return;
    if (!/^image\/(jpeg|png)$/.test(file.type)) {
      this._thumbStatus = "Only JPG and PNG images can be used as a thumbnail.";
      return;
    }
    if (file.size > MAX_THUMBNAIL_BYTES) {
      this._thumbStatus = "Max 10 MB";
      return;
    }
    this._thumbBusy = true;
    this._thumbStatus = "Uploading…";
    let dataUri: string;
    try {
      dataUri = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result));
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(file);
      });
    } catch {
      this._thumbBusy = false;
      this._thumbStatus = "Could not read the image file.";
      return;
    }
    const { data, error } = await tryExecute(this, postLibraryClipsByIdThumbnails({ path: { id }, body: { data: dataUri } }), { disableNotifications: true });
    this._thumbBusy = false;
    if (error || !data) {
      this._thumbStatus = saveError(error, "thumbnail upload");
      return;
    }
    this.#changed = true;
    this._thumbs = data.thumbnails;
    this._thumbStatus = data.notice ?? "Uploaded.";
  }

  // ---- subtitles --------------------------------------------------------------------------------------------------

  async #uploadSubtitle() {
    const id = this._clip?.id;
    const file = this._subFile;
    this._subError = undefined;
    this._subStatus = undefined;
    if (!id) return;
    if (!this._subIso) {
      this._subError = "Choose a language.";
      return;
    }
    if (!file) {
      this._subError = "Choose a .vtt or .srt file.";
      return;
    }
    if (!/\.(vtt|srt)$/i.test(file.name)) {
      this._subError = "Only .vtt and .srt files are supported";
      return;
    }
    if (file.size > MAX_SUBTITLE_BYTES) {
      this._subError = "Subtitle file too large (max 1 MB)";
      return;
    }
    this._subBusy = true;
    const content = await file.text();
    const { data, error } = await tryExecute(
      this,
      postLibraryClipsByIdSubtitles({ path: { id }, body: { isoCode: this._subIso, name: this._subName, default: this._subDefault, publish: this._subPublish, fileName: file.name, content } }),
      { disableNotifications: true },
    );
    this._subBusy = false;
    if (error || !data) {
      this._subError = saveError(error, "subtitle upload");
      return;
    }
    this._subs = data;
    this._subStatus = "Subtitle uploaded.";
    this._subFile = undefined;
    this._subName = "";
    this._subDefault = false;
    this._subPublish = false;
    const input = this.renderRoot.querySelector<HTMLInputElement>("#bb-sub-file");
    if (input) input.value = "";
  }

  async #setDefault(sub: SubtitleResponseModel) {
    const id = this._clip?.id;
    if (!id) return;
    this._subError = undefined;
    this._subStatus = undefined;
    this._subBusy = true;
    const { data, error } = await tryExecute(this, postLibraryClipsByIdSubtitlesBySubtitleIdDefault({ path: { id, subtitleId: sub.id }, body: { confirm: true } }), { disableNotifications: true });
    this._subBusy = false;
    if (error || !data) {
      this._subError = saveError(error, "default subtitle change");
      return;
    }
    this._subs = data;
    this._subStatus = "Default subtitle set.";
  }

  async #deleteSubtitle(sub: SubtitleResponseModel) {
    const id = this._clip?.id;
    if (!id) return;
    try {
      await umbConfirmModal(this, { headline: "Delete subtitle", content: `Delete the subtitle "${sub.name || sub.isoCode || sub.id}"? This cannot be undone.`, color: "danger", confirmLabel: "Delete" });
    } catch {
      return;
    }
    this._subError = undefined;
    this._subStatus = undefined;
    this._subBusy = true;
    const { data, error } = await tryExecute(this, deleteLibraryClipsByIdSubtitlesBySubtitleId({ path: { id, subtitleId: sub.id } }), { disableNotifications: true });
    this._subBusy = false;
    if (error || !data) {
      this._subError = statusOf(error) === 403 ? "You do not have permission to delete subtitles." : saveError(error, "subtitle deletion");
      return;
    }
    this._subs = data;
    this._subStatus = "Subtitle deleted.";
  }

  // ---- render -----------------------------------------------------------------------------------------------------

  #select(id: string, label: string, options: Option[], value: string, onChange: (v: string) => void) {
    // The current value stays selectable even when the lookup did not return it, so it is never lost on save.
    const all = options.some((o) => o.value === value) || value === "" ? options : [...options, { name: value, value }];
    return html`<uui-select id=${id} label=${label} .options=${all.map((o) => ({ ...o, selected: o.value === value }))} @change=${(e: Event) => onChange((e.target as HTMLSelectElement).value)}></uui-select>`;
  }

  #renderThumbnails() {
    return html`<uui-box headline="Thumbnails">
      <div class="thumbs" id="bb-thumbs">
        ${this._thumbs.map(
          (t) => html`<figure class=${t.main ? "thumb main" : "thumb"} data-src=${t.src ?? ""}>
            ${t.url ? html`<img src=${t.url} alt="" loading="lazy" />` : html`<div class="pending">New image</div>`}
            <figcaption>
              ${t.main ? html`<uui-tag look="primary" compact>Main</uui-tag>` : nothing}
              ${t.unconfirmed ? html`<uui-tag look="secondary" compact>Not confirmed yet</uui-tag>` : nothing}
              ${!t.main && t.src
                ? html`<uui-button class="set-main" look="secondary" compact label="Set as main" ?disabled=${this._thumbBusy} @click=${() => void this.#setMain(t)}>Set as main</uui-button>`
                : nothing}
            </figcaption>
          </figure>`,
        )}
        ${this._thumbs.length === 0 ? html`<p class="hint">This clip has no thumbnails yet.</p>` : nothing}
      </div>
      <div class="row">
        <label class="file">Upload new thumbnail (JPG or PNG, max 10 MB)
          <input id="bb-thumb-file" type="file" accept="image/jpeg,image/png" ?disabled=${this._thumbBusy} @change=${(e: Event) => void this.#uploadThumbnail(e)} />
        </label>
        <span class="status" id="bb-thumb-status" role="status" aria-live="polite">${this._thumbStatus ?? ""}</span>
      </div>
      <p class="hint">The player can take up to about two minutes to show a new thumbnail.</p>
    </uui-box>`;
  }

  #renderCustomFields() {
    if (!this._clip?.customFieldsEnabled) return nothing;
    if (this._groups.length === 0) return html`<uui-box headline="Custom fields"><p class="hint">No custom field groups are defined for clips in this publication.</p></uui-box>`;
    return html`${this._groups.map(
      (g) => html`<uui-box headline=${g.title} class="cf-group">
        ${g.fields.map(
          (f) => html`<label class="field">${f.label}
            ${f.iType === "text" || f.type === "text"
              ? html`<uui-textarea label=${f.label} data-field=${f.name} .value=${this._custom[f.name] ?? ""} @input=${(e: Event) => (this._custom = { ...this._custom, [f.name]: (e.target as HTMLTextAreaElement).value })}></uui-textarea>`
              : html`<uui-input label=${f.label} data-field=${f.name} .value=${this._custom[f.name] ?? ""} @input=${(e: Event) => (this._custom = { ...this._custom, [f.name]: (e.target as HTMLInputElement).value })}></uui-input>`}
          </label>`,
        )}
      </uui-box>`,
    )}`;
  }

  #renderSubtitles() {
    const langOptions: Option[] = [{ name: "Select a language…", value: "" }, ...this._languages.map((l) => ({ name: `${l.name} (${l.isoCode})`, value: l.isoCode }))];
    return html`<uui-box headline="Subtitles">
      <ul class="subs" id="bb-subs">
        ${this._subs.map(
          (s) => html`<li data-subtitle-id=${s.id}>
            <span class="sub-name">${s.name || s.languageName || s.isoCode || `Subtitle ${s.id}`}</span>
            <span class="sub-lang">${s.isoCode ?? ""}</span>
            ${s.default ? html`<uui-tag look="primary" compact>Default</uui-tag>` : nothing}
            ${s.status && s.status !== "published" ? html`<uui-tag look="secondary" compact>${s.status}</uui-tag>` : nothing}
            <span class="spacer"></span>
            ${!s.default ? html`<uui-button class="sub-default" look="secondary" compact label="Set default" ?disabled=${this._subBusy} @click=${() => void this.#setDefault(s)}>Set default</uui-button>` : nothing}
            ${this._canDelete ? html`<uui-button class="sub-delete" look="secondary" color="danger" compact label="Delete" ?disabled=${this._subBusy} @click=${() => void this.#deleteSubtitle(s)}>Delete</uui-button>` : nothing}
          </li>`,
        )}
        ${this._subs.length === 0 ? html`<li class="hint">No subtitles yet.</li>` : nothing}
      </ul>
      <div class="sub-form">
        <label class="field">Language${this.#select("bb-sub-language", "Subtitle language", langOptions, this._subIso, (v) => (this._subIso = v))}</label>
        <label class="field">Name (optional)
          <uui-input id="bb-sub-name" label="Subtitle name" maxlength="100" .value=${this._subName} @input=${(e: Event) => (this._subName = (e.target as HTMLInputElement).value)}></uui-input>
        </label>
        <label class="file">File (.vtt or .srt, max 1 MB)
          <input id="bb-sub-file" type="file" accept=".vtt,.srt,text/vtt,application/x-subrip" @change=${(e: Event) => (this._subFile = (e.target as HTMLInputElement).files?.[0])} />
        </label>
        <div class="checks">
          <uui-checkbox id="bb-sub-publish" label="Publish" ?checked=${this._subPublish} @change=${(e: Event) => (this._subPublish = (e.target as HTMLInputElement).checked)}>Publish</uui-checkbox>
          <uui-checkbox id="bb-sub-default" label="Make default" ?checked=${this._subDefault} @change=${(e: Event) => (this._subDefault = (e.target as HTMLInputElement).checked)}>Make default</uui-checkbox>
        </div>
        <uui-button id="bb-sub-upload" look="secondary" label="Upload subtitle" ?disabled=${this._subBusy} @click=${() => void this.#uploadSubtitle()}>Upload subtitle</uui-button>
      </div>
      ${this._subError ? html`<p class="error" id="bb-sub-error" role="alert">${this._subError}</p>` : nothing}
      ${this._subStatus ? html`<p class="ok" id="bb-sub-status" role="status">${this._subStatus}</p>` : nothing}
    </uui-box>`;
  }

  #close() {
    this.value = { changed: this.#changed };
    this._submitModal();
  }

  override render() {
    const clip = this._clip;
    const statusOptions = STATUSES.map((s) => ({ name: s[0].toUpperCase() + s.slice(1), value: s }));
    const cppOptions: Option[] = [{ name: "None", value: "" }, ...this._policies.map((p) => ({ name: p.name, value: p.code }))];
    const playoutOptions: Option[] = [{ name: "Publication default", value: "" }, ...this._playouts.map((p) => ({ name: p.label, value: p.value }))];
    const folderOptions: Option[] = [{ name: "No folder (root)", value: "" }, ...flatten(this._folders)];
    const languageOptions: Option[] = [{ name: "Not set", value: "" }, ...this._languages.map((l) => ({ name: `${l.name} (${l.isoCode})`, value: l.isoCode }))];
    return html`<umb-body-layout headline=${clip ? `Edit: ${clip.title}` : "Edit video"}>
      ${this._loadError ? html`<uui-box><p class="error" id="bb-edit-load-error" role="alert">${this._loadError}</p></uui-box>` : nothing}
      ${clip
        ? html`${this.#renderThumbnails()}
            <uui-box headline="Details">
              <div class="form">
                <label class="field">Title
                  <uui-input id="bb-edit-title" label="Title" maxlength="255" .value=${this._title} @input=${(e: Event) => (this._title = (e.target as HTMLInputElement).value)}></uui-input>
                </label>
                <label class="field">Description
                  <uui-textarea id="bb-edit-description" label="Description" .value=${this._description} @input=${(e: Event) => (this._description = (e.target as HTMLTextAreaElement).value)}></uui-textarea>
                </label>
                <div class="field">Tags
                  <bb-tag-chips id="bb-edit-tags" .tags=${this._tags} @change=${(e: CustomEvent<string[]>) => (this._tags = e.detail)}></bb-tag-chips>
                </div>
                <label class="field">Status ${this.#select("bb-edit-status", "Status", statusOptions, this._status, (v) => (this._status = v))}</label>
                <label class="field">Content protection policy ${this.#select("bb-edit-cpp", "Content protection policy", cppOptions, this._cpp, (v) => (this._cpp = v))}</label>
                <label class="field">Playout ${this.#select("bb-edit-playout", "Playout", playoutOptions, this._playout, (v) => (this._playout = v))}
                  <small class="hint">Used for this video's embed code and preview in the library.</small>
                </label>
                <label class="field">Folder ${this.#select("bb-edit-folder", "Folder", folderOptions, this._folder, (v) => (this._folder = v))}</label>
                <label class="field">Language ${this.#select("bb-edit-language", "Language", languageOptions, this._language, (v) => (this._language = v))}</label>
                <div class="dates">
                  <label class="field">Embargo from (UTC)
                    <uui-input id="bb-edit-embargo-from" type="datetime-local" label="Embargo from" .value=${this._embargoFrom} @input=${(e: Event) => (this._embargoFrom = (e.target as HTMLInputElement).value)}></uui-input>
                  </label>
                  <label class="field">Embargo to (UTC)
                    <uui-input id="bb-edit-embargo-to" type="datetime-local" label="Embargo to" .value=${this._embargoTo} @input=${(e: Event) => (this._embargoTo = (e.target as HTMLInputElement).value)}></uui-input>
                  </label>
                </div>
              </div>
            </uui-box>
            ${this.#renderCustomFields()} ${this.#renderSubtitles()}
            <bb-cta-editor .clipId=${clip.id}></bb-cta-editor>
            <bb-clip-analytics .clipId=${clip.id}></bb-clip-analytics>`
        : this._loadError
          ? nothing
          : html`<uui-loader-bar></uui-loader-bar>`}
      <div slot="actions">
        ${this._error ? html`<span class="error" id="bb-edit-error" role="alert">${this._error}</span>` : nothing}
        ${this._message ? html`<span class="ok" id="bb-edit-message" role="status">${this._message}</span>` : nothing}
        ${clip ? html`<uui-button id="bb-edit-save" label="Save" look="primary" color="positive" ?disabled=${this._saving || !this.#dirty} @click=${() => void this.#save()}>Save</uui-button>` : nothing}
        <uui-button id="bb-edit-close" label=${this.#dirty ? "Cancel" : "Close"} @click=${() => this.#close()}>${this.#dirty ? "Cancel" : "Close"}</uui-button>
      </div>
    </umb-body-layout>`;
  }

  static override styles = css`
    uui-box { margin-bottom: var(--uui-size-space-4); }
    .form { display: grid; gap: var(--uui-size-space-4); }
    .field { display: grid; gap: var(--uui-size-space-1); font-weight: 600; }
    .field uui-input, .field uui-textarea, .field uui-select { font-weight: normal; }
    .dates { display: grid; grid-template-columns: 1fr 1fr; gap: var(--uui-size-space-4); }
    .hint { color: var(--uui-color-text-alt); font-weight: normal; font-size: 12px; margin: var(--uui-size-space-2) 0 0; }
    .error { color: var(--uui-color-danger-standalone); margin: 0 var(--uui-size-space-3); }
    .ok { color: var(--uui-color-positive-standalone); margin: 0 var(--uui-size-space-3); }
    .thumbs { display: flex; flex-wrap: wrap; gap: var(--uui-size-space-3); margin-bottom: var(--uui-size-space-3); }
    .thumb { margin: 0; width: 150px; }
    .thumb img, .thumb .pending { display: block; width: 150px; aspect-ratio: 16 / 9; object-fit: cover; background: #000; border: 2px solid transparent; border-radius: var(--uui-border-radius); }
    .thumb.main img { border-color: var(--uui-color-selected); }
    .thumb .pending { color: #fff; display: flex; align-items: center; justify-content: center; font-size: 12px; }
    .thumb figcaption { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 4px; min-height: 24px; }
    .row { display: flex; align-items: center; gap: var(--uui-size-space-4); flex-wrap: wrap; }
    .file { display: grid; gap: var(--uui-size-space-1); font-weight: 600; font-size: 13px; }
    .file input { font-weight: normal; }
    .status { color: var(--uui-color-text-alt); }
    .subs { list-style: none; padding: 0; margin: 0 0 var(--uui-size-space-4); display: grid; gap: var(--uui-size-space-2); }
    .subs li { display: flex; align-items: center; gap: var(--uui-size-space-2); }
    .sub-name { font-weight: 600; }
    .sub-lang { color: var(--uui-color-text-alt); }
    .spacer { flex: 1; }
    .sub-form { display: grid; gap: var(--uui-size-space-3); max-width: 480px; }
    .checks { display: flex; gap: var(--uui-size-space-5); }
    [slot="actions"] { display: flex; align-items: center; }
  `;
}

export default BbClipEditModalElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-clip-edit-modal": BbClipEditModalElement;
  }
}
