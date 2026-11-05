import { css, customElement, html, LitElement, nothing, state, unsafeCSS } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import Uppy from "@uppy/core";
import Dashboard from "@uppy/dashboard";
import AwsS3 from "@uppy/aws-s3";
// Uppy's styles are plain CSS files. The backoffice renders in shadow DOM, where page styles do not reach, so the
// CSS is imported as text and added to this element's own styles.
import uppyCoreCss from "@uppy/core/css/style.min.css?inline";
import uppyDashboardCss from "@uppy/dashboard/css/style.min.css?inline";
import { postUploadAbort, postUploadComplete, postUploadInit, postUploadSignPart, type InitUploadResponseModel } from "../api/index.js";
import { statusOf } from "./errors.js";
import type { Term } from "../localization/terms.js";
import { ALLOWED_TYPES, limitsText, MAX_FILE_SIZE } from "./upload-limits.js";
import "../shared/credentials-banner.element.js";

/** A part that never reached storage shows up as "Unknown error"; the usual cause is storage that does not allow this site's address. */
function uploadErrorText(message: string | undefined, t: Term): string {
  return !message || /unknown error|network/i.test(message) ? t("bluebillywig_uploadStorageError") : message;
}

const FALLBACK_PART_SIZE = 50 * 1024 * 1024;
const STATUSES = ["published", "draft", "archived"] as const;

/**
 * Upload view. Direct browser-to-storage multipart upload: the server creates the clip and asks Blue Billywig for
 * presigned part URLs (init), and only small JSON calls pass through Umbraco (init, sign part, complete, abort).
 * Uppy PUTs the parts straight to the storage host. Port of the reference plugin's bb_upload.js.
 *
 * The clip is created when Upload is pressed, not when a file is added, so choosing a file and then removing it
 * leaves nothing behind. A cancelled or failed upload asks the server to delete its placeholder clip.
 */
@customElement("bb-library-upload")
export class BbLibraryUploadElement extends UmbElementMixin(LitElement) {
  @state() private _title = "";
  @state() private _description = "";
  @state() private _tags = "";
  @state() private _status: (typeof STATUSES)[number] = "published";
  @state() private _error?: string;
  @state() private _done: Array<{ name: string; clipId: number }> = [];

  #uppy?: Uppy;
  /** The upload that is in progress. One clip per upload: the fields above describe a single clip. */
  #init?: InitUploadResponseModel;
  #aborted = new Set<number>();

  #t: Term = (key, ...args) => this.localize.term(key, ...args);

  override firstUpdated() {
    const target = this.renderRoot.querySelector<HTMLElement>("#bb-dashboard");
    if (!target) return;

    const uppy = new Uppy({
      autoProceed: false,
      allowMultipleUploadBatches: true,
      restrictions: { maxNumberOfFiles: 1, maxFileSize: MAX_FILE_SIZE, allowedFileTypes: ALLOWED_TYPES },
    });
    this.#uppy = uppy;

    uppy.use(Dashboard, {
      inline: true,
      target,
      height: 360,
      width: "100%",
      proudlyDisplayPoweredByUppy: false,
      note: this.#t("bluebillywig_uploadNote"),
    });

    // Runs when Upload is pressed: creates the clip and starts the multipart upload on the server.
    uppy.addPreProcessor(async (fileIds) => {
      const file = uppy.getFile(fileIds[0]);
      if (!file) return;
      this.#init = await this.#initUpload(file.name ?? "", file.size ?? 0, file.type || "application/octet-stream");
    });

    uppy.use(AwsS3, {
      shouldUseMultipart: true,
      limit: 3,
      // The part size is fixed by the server: the presigned URLs were made for exactly this size.
      getChunkSize: () => this.#init?.partSize ?? FALLBACK_PART_SIZE,
      createMultipartUpload: async () => {
        if (!this.#init) throw new Error("The upload was not prepared");
        return { uploadId: this.#init.uploadId, key: this.#init.key ?? "" };
      },
      signPart: async (_file, { partNumber }) => {
        const cached = this.#init?.presignedUrls.find((p) => p.partNumber === partNumber);
        if (cached) return { url: cached.url };
        const { data } = await postUploadSignPart({ body: { uploadId: this.#init?.uploadId, partNumber } });
        return { url: data.url };
      },
      listParts: async () => [],
      completeMultipartUpload: async (_file, { uploadId, parts }) => {
        const { data } = await postUploadComplete({
          body: { uploadId, parts: parts.map((p) => ({ PartNumber: p.PartNumber ?? 0, ETag: p.ETag ?? "" })) },
        });
        return { location: data.location ?? undefined };
      },
      abortMultipartUpload: async () => {
        await this.#abort();
      },
    });

    uppy.on("complete", (result) => {
      const clipId = this.#init?.mediaclipId;
      if (result.successful?.length && clipId) {
        this._done = [...this._done, ...result.successful.map((f) => ({ name: f.name ?? "", clipId }))];
        this._error = undefined;
        this.#init = undefined;
        uppy.clear?.();
      }
    });

    // A refused file (wrong type, over the size limit, a second file) is said in the page too: Uppy's own notice is a
    // toast inside the dashboard that disappears after a few seconds.
    uppy.on("restriction-failed", (_file, error) => {
      this._error = error.message;
    });
    uppy.on("file-added", () => {
      this._error = undefined;
    });

    uppy.on("error", (error) => {
      this._error = uploadErrorText(error.message, this.#t);
      void this.#abort();
    });

    uppy.on("upload-error", (_file, error) => {
      this._error = uploadErrorText(error.message, this.#t);
      void this.#abort();
    });
  }

  override disconnectedCallback() {
    super.disconnectedCallback();
    this.#uppy?.destroy();
    this.#uppy = undefined;
  }

  async #initUpload(filename: string, filesize: number, contentType: string) {
    try {
      const { data } = await postUploadInit({
        body: {
          filename,
          filesize,
          contentType,
          title: this._title.trim() || undefined,
          description: this._description.trim() || undefined,
          tags: this._tags.trim() || undefined,
          status: this._status,
        },
      });
      return data;
    } catch (error) {
      throw new Error(this.#t(statusOf(error) === 400 ? "bluebillywig_uploadDetailsRejected" : "bluebillywig_uploadPrepareFailed"));
    }
  }

  /** Removes the placeholder clip of a cancelled or failed upload, once. The server refuses anything it did not start. */
  async #abort() {
    const clipId = this.#init?.mediaclipId;
    if (!clipId || this.#aborted.has(clipId)) return;
    this.#aborted.add(clipId);
    this.#init = undefined;
    try {
      await postUploadAbort({ body: { mediaclipId: clipId } });
    } catch {
      this._error = this.#t("bluebillywig_uploadAbortFailed", clipId);
    }
  }

  override render() {
    return html`<bb-credentials-banner></bb-credentials-banner><uui-box headline=${this.#t("bluebillywig_tabUpload")}>
        <div class="form">
          <label>${this.#t("bluebillywig_uploadTitleField")}
            <uui-input id="bb-upload-title" label=${this.#t("bluebillywig_title")} maxlength="255" .value=${this._title} @input=${(e: Event) => (this._title = (e.target as HTMLInputElement).value)}></uui-input>
          </label>
          <label>${this.#t("bluebillywig_description")}
            <uui-textarea id="bb-upload-description" label=${this.#t("bluebillywig_description")} .value=${this._description} @input=${(e: Event) => (this._description = (e.target as HTMLTextAreaElement).value)}></uui-textarea>
          </label>
          <label>${this.#t("bluebillywig_uploadTagsField")}
            <uui-input id="bb-upload-tags" label=${this.#t("bluebillywig_tags")} .value=${this._tags} @input=${(e: Event) => (this._tags = (e.target as HTMLInputElement).value)}></uui-input>
          </label>
          <label>${this.#t("bluebillywig_status")}
            <uui-select
              id="bb-upload-status"
              label=${this.#t("bluebillywig_status")}
              .options=${STATUSES.map((s) => ({ name: this.#t("bluebillywig_status" + s[0].toUpperCase() + s.slice(1)), value: s, selected: s === this._status }))}
              @change=${(e: Event) => (this._status = (e.target as HTMLSelectElement).value as (typeof STATUSES)[number])}></uui-select>
          </label>
        </div>
        ${this._error ? html`<p class="error" id="bb-upload-error" role="alert">${this._error}</p>` : nothing}
        ${this._done.map((d) => html`<p class="ok" data-clip-id=${d.clipId}>${this.#t("bluebillywig_uploadDone", d.name, d.clipId)}</p>`)}
        <p class="limits" id="bb-upload-limits">${limitsText(this.#t)}</p>
        <div id="bb-dashboard"></div>
      </uui-box>`;
  }

  static override styles = [
    css`
      :host { display: block; padding: var(--uui-size-layout-1); }
      .form { display: grid; gap: var(--uui-size-space-4); margin-bottom: var(--uui-size-space-5); max-width: 640px; }
      label { display: grid; gap: var(--uui-size-space-1); }
      .error { color: var(--uui-color-danger-standalone); }
      .limits { color: var(--uui-color-text-alt); font-size: 12px; margin: 0 0 var(--uui-size-space-3); }
      .ok { color: var(--uui-color-positive-standalone); }
    `,
    unsafeCSS(uppyCoreCss),
    unsafeCSS(uppyDashboardCss),
  ];
}

export default BbLibraryUploadElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-library-upload": BbLibraryUploadElement;
  }
}
