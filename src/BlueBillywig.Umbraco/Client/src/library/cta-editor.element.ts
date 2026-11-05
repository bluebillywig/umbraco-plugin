import { css, customElement, html, LitElement, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import {
  postLibraryClipsByIdCtaRemove,
  getLibraryClips,
  getLibraryClipsByIdCta,
  postLibraryCtaImages,
  postLibraryCtaValidate,
  putLibraryClipsByIdCta,
  type CtaConfig,
  type LibraryClipResponseModel,
} from "../api/index.js";
import { statusOf } from "./errors.js";
import { tabTarget } from "../shared/accessibility.js";

type Mode = "off" | "simple" | "canvas";

/** The page's own image cap: the server refuses anything bigger, this saves the round trip. */
const MAX_IMAGE_BYTES = 7 * 1024 * 1024;

/**
 * Position presets as percentages of the 1920x1080 design canvas. The reference plugin's four, plus "custom" for
 * whatever the canvas produced.
 */
const PRESETS: Record<string, { x: number; y: number; w: number; h: number; label: string }> = {
  "bottom-center": { x: 25, y: 75, w: 50, h: 12, label: "Bottom center" },
  "bottom-right": { x: 55, y: 75, w: 40, h: 12, label: "Bottom right" },
  "top-right": { x: 55, y: 8, w: 40, h: 12, label: "Top right" },
  "end-screen": { x: 15, y: 25, w: 70, h: 50, label: "End-screen fullbleed" },
};

const FONTS = ["Verdana", "Arial", "Helvetica", "Georgia", "Courier"];

/** A new call to action starts from the editor defaults of the reference plugin (its DEFAULT_CTA), not the server's absent-key defaults. */
function defaults(clipLength: number): CtaConfig {
  return {
    timelineId: "",
    title: "Call to action",
    ctaType: "link",
    headline: "",
    body: "",
    buttonLabel: "Learn more",
    url: "",
    openInNewTab: true,
    pauseOnShow: false,
    formEmailLabel: "Email address",
    formSubmitLabel: "Submit",
    formThankYou: "Thanks!",
    formSubmitEndpoint: "",
    backgroundColor: "002837",
    hoverColor: "33535f",
    textColor: "FFFFFF",
    fontFamily: "Verdana",
    fontSize: 3.2,
    cornerRadius: 1,
    borderThickness: 0,
    borderColor: "FFFFFF",
    backgroundAlpha: 100,
    backgroundImageId: "",
    hoverImageId: "",
    xpos: 25,
    ypos: 75,
    width: 50,
    height: 12,
    startAt: 0,
    endAt: 0,
    clipLength,
  };
}

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));
const hash = (c?: string | null) => (c ? `#${c.replace(/^#/, "")}` : "");
const strip = (c: string) => c.replace(/^#/, "");

/** A hex colour for an `<input type="color">`, which only takes the 6-digit form; anything else shows as black. */
function colorValue(c?: string | null): string {
  const s = (c ?? "").replace(/^#/, "");
  if (/^[0-9a-fA-F]{6}$/.test(s)) return `#${s}`;
  if (/^[0-9a-fA-F]{3}$/.test(s)) return `#${s[0]}${s[0]}${s[1]}${s[1]}${s[2]}${s[2]}`;
  return "#000000";
}

/**
 * The "Call to action" section of the clip editor. Port of the reference plugin's CTA editor (bb-cta-editor.js): Off,
 * Simple (colours, position preset, sizes, timing, text, link, image, form) and Canvas (the same state, with a surface to drag
 * and resize the call to action on). It has its own Save and Remove, because a call to action is a timeline of its own in Blue
 * Billywig, not a field of the clip.
 *
 * What the server decides, this shows: the rule messages come live from the server's own validator, a clip with a timeline
 * from another tool is locked (a save would be refused with 409), and what is shown after a save is what was stored.
 * The whole section is absent when the settings turn the feature off (the server answers 404).
 */
@customElement("bb-cta-editor")
export class BbCtaEditorElement extends UmbElementMixin(LitElement) {
  #t = (key: string, ...args: unknown[]) => this.localize.term(key, ...args);

  @property({ type: Number }) clipId = 0;

  @state() private _enabled = true;
  @state() private _loaded = false;
  @state() private _loadError?: string;
  @state() private _cfg: CtaConfig = defaults(0);
  @state() private _mode: Mode = "off";
  @state() private _preset = "bottom-center";
  @state() private _timelineId = "";
  @state() private _clipLength = 0;
  @state() private _foreignId?: string;
  @state() private _busy = false;
  @state() private _status?: { text: string; kind: "ok" | "error" };
  @state() private _errors: string[] = [];
  @state() private _imageStatus?: string;
  @state() private _imageResults: LibraryClipResponseModel[] = [];
  @state() private _imageSearched = false;

  #validateSeq = 0;
  #validateTimer?: ReturnType<typeof setTimeout>;
  #searchTimer?: ReturnType<typeof setTimeout>;
  #drag?: { mode: "move" | "resize"; startX: number; startY: number; box: DOMRect; x: number; y: number; w: number; h: number };

  override connectedCallback() {
    super.connectedCallback();
    void this.#load();
  }

  override disconnectedCallback() {
    super.disconnectedCallback();
    clearTimeout(this.#validateTimer);
    clearTimeout(this.#searchTimer);
  }

  /** Every call reports its own result inside the section, so the backoffice's generic error toast (which a 404 for "turned off" would also raise) is switched off. */
  #call<T>(promise: Promise<T>) {
    return tryExecute(this, promise, { disableNotifications: true });
  }

  // ---- load / save / delete -----------------------------------------------------------------------------------------

  async #load() {
    if (!this.clipId) return;
    const { data, error } = await this.#call(getLibraryClipsByIdCta({ path: { id: this.clipId } }));
    if (error || !data) {
      // 404 is how the server says the feature is switched off in the settings: the section is simply not there.
      if (statusOf(error) === 404) {
        this._enabled = false;
        return;
      }
      this._loadError = this.#t(statusOf(error) === 502 ? "bluebillywig_ctaLoadGateway" : "bluebillywig_ctaLoadFailed");
      return;
    }
    this._clipLength = data.clipLength;
    this._foreignId = data.foreignTimeline ? data.foreignTimelineId || "?" : undefined;
    this.#apply(data.cta ?? null, data.timelineId);
    if (this._foreignId) {
      this._status = { text: this.#t("bluebillywig_ctaForeign", this._foreignId), kind: "error" };
    }
    this._loaded = true;
  }

  /** Shows a stored call to action, or the empty state. */
  #apply(cta: CtaConfig | null, timelineId: string) {
    this._timelineId = timelineId;
    if (cta) {
      this._cfg = { ...cta, clipLength: this._clipLength };
      this._preset = this.#presetOf(cta);
      this._mode = "simple";
    } else {
      this._cfg = defaults(this._clipLength);
      this._preset = "bottom-center";
      this._mode = "off";
    }
    this._errors = [];
  }

  #presetOf(c: CtaConfig): string {
    for (const [name, p] of Object.entries(PRESETS)) {
      if (c.xpos === p.x && c.ypos === p.y && c.width === p.w && c.height === p.h) return name;
    }
    return "custom";
  }

  async #save() {
    if (this._foreignId || this._busy) return;
    if (this._mode === "off") {
      // Off plus Save removes the call to action, like the plugin.
      if (this._timelineId) await this.#delete();
      return;
    }
    const cfg = { ...this._cfg, clipLength: this._clipLength };
    // An image CTA with no height would collapse: the player positions it absolutely.
    if (cfg.backgroundImageId && !cfg.height) cfg.height = Math.round(cfg.width * (16 / 9));
    this._busy = true;
    this._status = { text: this.#t("bluebillywig_saving"), kind: "ok" };
    const { data, error } = await this.#call(putLibraryClipsByIdCta({ path: { id: this.clipId }, body: cfg }));
    this._busy = false;
    if (error || !data) {
      this._status = { text: this.#errorText(error, this.#t("bluebillywig_ctaSaveFailed")), kind: "error" };
      const errors = (error as { problemDetails?: { errors?: string[] } } | undefined)?.problemDetails?.errors;
      if (statusOf(error) === 400 && Array.isArray(errors)) this._errors = errors;
      if (statusOf(error) === 409) await this.#load();
      return;
    }
    this._clipLength = data.clipLength;
    this.#apply(data.cta, data.timelineId);
    this._status = { text: this.#t("bluebillywig_saved"), kind: "ok" };
  }

  async #delete() {
    if (this._foreignId || this._busy) return;
    try {
      await umbConfirmModal(this, { headline: this.#t("bluebillywig_ctaRemove"), content: this.#t("bluebillywig_ctaRemoveContent"), color: "danger", confirmLabel: this.#t("bluebillywig_remove") });
    } catch {
      return;
    }
    this._busy = true;
    this._status = { text: this.#t("bluebillywig_removing"), kind: "ok" };
    const { data, error } = await this.#call(postLibraryClipsByIdCtaRemove({ path: { id: this.clipId }, body: { confirm: true } }));
    this._busy = false;
    if (error || !data) {
      this._status = { text: this.#errorText(error, this.#t("bluebillywig_ctaDeleteFailed")), kind: "error" };
      if (statusOf(error) === 409) await this.#load();
      return;
    }
    this.#apply(null, "");
    this._status = { text: this.#t(data.deleted ? "bluebillywig_removed" : "bluebillywig_ctaNothingToRemove"), kind: "ok" };
  }

  #errorText(error: unknown, fallback: string): string {
    const status = statusOf(error);
    if (status === 502) return this.#t("bluebillywig_ctaSaveGateway");
    if (status === 401 || status === 403) return this.#t("bluebillywig_ctaNoPermission");
    if (status === 404) return this.#t("bluebillywig_ctaGone");
    // Our own refusals (400, 409) carry the sentence for the editor as problem details.
    const detail = (error as { problemDetails?: { detail?: unknown } } | undefined)?.problemDetails?.detail;
    if ([400, 409].includes(status) && typeof detail === "string" && detail.length > 0) return detail;
    return fallback;
  }

  // ---- state helpers --------------------------------------------------------------------------------------------------

  #set(patch: Partial<CtaConfig>) {
    this._cfg = { ...this._cfg, ...patch };
    this.#scheduleValidate();
  }

  #setPosition(x: number, y: number, w: number, h: number) {
    this._preset = "custom";
    this.#set({ xpos: x, ypos: y, width: w, height: h });
  }

  #pickPreset(name: string) {
    this._preset = name;
    const p = PRESETS[name];
    if (p) this.#set({ xpos: p.x, ypos: p.y, width: p.w, height: p.h });
  }

  /** The server's rules, asked as the editor types (after a short pause), so the messages never differ from what a save would say. */
  #scheduleValidate() {
    clearTimeout(this.#validateTimer);
    this.#validateTimer = setTimeout(() => void this.#validate(), 300);
  }

  async #validate() {
    if (this._mode === "off" || this._foreignId) {
      this._errors = [];
      return;
    }
    const seq = ++this.#validateSeq;
    const { data } = await this.#call(postLibraryCtaValidate({ body: { ...this._cfg, clipLength: this._clipLength } }));
    if (seq !== this.#validateSeq) return;
    // A failed check says nothing: the save will say it.
    if (data) this._errors = data.errors;
  }

  #setMode(mode: Mode) {
    this._mode = mode;
    this.#scheduleValidate();
  }

  // ---- image --------------------------------------------------------------------------------------------------------------

  async #uploadImage(e: Event) {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = "";
    if (!file) return;
    if (!/^image\/(jpeg|png|gif|webp)$/.test(file.type)) {
      this._imageStatus = "Use a JPG, PNG, GIF or WebP image.";
      return;
    }
    if (file.size > MAX_IMAGE_BYTES) {
      this._imageStatus = "Image too large (max 7 MB)";
      return;
    }
    this._imageStatus = "Uploading…";
    let data: string;
    try {
      data = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result));
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(file);
      });
    } catch {
      this._imageStatus = "Could not read the image file.";
      return;
    }
    const result = await this.#call(postLibraryCtaImages({ body: { data } }));
    if (result.error || !result.data) {
      this._imageStatus = this.#errorText(result.error, "The image could not be uploaded.");
      return;
    }
    this.#set({ backgroundImageId: result.data.imageId });
    this._imageStatus = `Uploaded (#${result.data.imageId}).`;
  }

  #searchImages(query: string) {
    clearTimeout(this.#searchTimer);
    if (!query.trim()) {
      this._imageResults = [];
      this._imageSearched = false;
      return;
    }
    this.#searchTimer = setTimeout(async () => {
      const { data } = await this.#call(getLibraryClips({ query: { search: query.trim(), mediatype: "image" } }));
      this._imageResults = (data?.items ?? []).filter((c) => c.mediaType === "image").slice(0, 12);
      this._imageSearched = true;
    }, 300);
  }

  // ---- canvas ------------------------------------------------------------------------------------------------------------

  #pointerDown(e: PointerEvent) {
    if (e.button !== 0 || this._foreignId) return;
    const widget = e.currentTarget as HTMLElement;
    const canvas = this.renderRoot.querySelector<HTMLElement>(".canvas");
    if (!canvas) return;
    const resize = (e.target as HTMLElement).classList.contains("handle");
    this.#drag = { mode: resize ? "resize" : "move", startX: e.clientX, startY: e.clientY, box: canvas.getBoundingClientRect(), x: this._cfg.xpos, y: this._cfg.ypos, w: this._cfg.width, h: this._cfg.height };
    widget.setPointerCapture(e.pointerId);
    e.preventDefault();
  }

  #pointerMove(e: PointerEvent) {
    const d = this.#drag;
    if (!d) return;
    const dx = ((e.clientX - d.startX) / d.box.width) * 100;
    const dy = ((e.clientY - d.startY) / d.box.height) * 100;
    if (d.mode === "move") {
      this.#setPosition(clamp(d.x + dx, 0, 100 - this._cfg.width), clamp(d.y + dy, 0, 100 - this._cfg.height), this._cfg.width, this._cfg.height);
    } else {
      this.#setPosition(this._cfg.xpos, this._cfg.ypos, clamp(d.w + dx, 5, 100 - this._cfg.xpos), clamp(d.h + dy, 3, 100 - this._cfg.ypos));
    }
  }

  #pointerUp(e: PointerEvent) {
    this.#drag = undefined;
    const widget = e.currentTarget as HTMLElement;
    if (widget.hasPointerCapture(e.pointerId)) widget.releasePointerCapture(e.pointerId);
  }

  /** Keyboard nudge, as in the plugin: arrows move by 1%, with shift by 5%. */
  #keyDown(e: KeyboardEvent) {
    if (this._foreignId) return;
    const step = e.shiftKey ? 5 : 1;
    const c = this._cfg;
    const moves: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    const move = moves[e.key];
    if (!move) return;
    e.preventDefault();
    this.#setPosition(clamp(c.xpos + move[0], 0, 100 - c.width), clamp(c.ypos + move[1], 0, 100 - c.height), c.width, c.height);
  }

  /** Arrow keys, Home and End move between the mode tabs and select the one reached (the tab pattern); only the selected tab is in the tab order. */
  #tabKey(e: KeyboardEvent, modes: [Mode, string][]) {
    const next = tabTarget(e.key, modes.findIndex(([m]) => m === this._mode), modes.length);
    if (next === undefined || this._foreignId) return;
    e.preventDefault();
    this.#setMode(modes[next][0]);
    void this.updateComplete.then(() => this.renderRoot.querySelector<HTMLElement>(`#bb-cta-mode-${modes[next][0]}`)?.focus());
  }

  // ---- render ------------------------------------------------------------------------------------------------------------

  #text(label: string, id: string, value: string, onInput: (v: string) => void, extra: { placeholder?: string; type?: string; maxlength?: number } = {}) {
    return html`<label class="field">${label}
      <uui-input id=${id} label=${label} type=${extra.type ?? "text"} maxlength=${extra.maxlength ?? 500} placeholder=${extra.placeholder ?? ""} .value=${value} @input=${(e: Event) => onInput((e.target as HTMLInputElement).value)}></uui-input>
    </label>`;
  }

  #number(label: string, id: string, value: number, onInput: (v: number) => void, attrs: { min?: number; max?: number; step?: number } = {}) {
    return html`<label class="field">${label}
      <input id=${id} type="number" .value=${String(value)} min=${attrs.min ?? nothing} max=${attrs.max ?? nothing} step=${attrs.step ?? "any"} @input=${(e: Event) => onInput(parseFloat((e.target as HTMLInputElement).value || "0"))} />
    </label>`;
  }

  #range(label: string, id: string, value: number, min: number, max: number, step: number, onInput: (v: number) => void) {
    return html`<label class="field">${label}: <output>${value}</output>
      <input id=${id} type="range" min=${min} max=${max} step=${step} .value=${String(value)} @input=${(e: Event) => onInput(parseFloat((e.target as HTMLInputElement).value))} />
    </label>`;
  }

  #color(label: string, id: string, value: string | null | undefined, onInput: (v: string) => void) {
    return html`<label class="field">${label}
      <input id=${id} type="color" .value=${colorValue(value)} @input=${(e: Event) => onInput(strip((e.target as HTMLInputElement).value))} />
    </label>`;
  }

  #renderCanvas() {
    const c = this._cfg;
    const text = c.ctaType === "form" ? `${c.formEmailLabel || "Email"}  [${c.formSubmitLabel || "Submit"}]` : [c.headline, c.body, c.buttonLabel].filter((t) => t.trim()).join(" / ");
    const style = [
      `left:${c.xpos}%`,
      `top:${c.ypos}%`,
      `width:${c.width}%`,
      `height:${c.height}%`,
      `background-color:${hash(c.backgroundColor)}`,
      `color:${hash(c.textColor)}`,
      `font-family:${FONTS.includes(c.fontFamily) ? c.fontFamily : "Verdana"}`,
      `font-size:${Math.max(8, c.fontSize * 2)}px`,
      `border-radius:${c.cornerRadius}%`,
      `border:${c.borderThickness}px solid ${hash(c.borderColor)}`,
      `opacity:${(c.backgroundAlpha / 100).toFixed(2)}`,
    ].join(";");
    return html`<div class="canvas-wrap">
      <div class="canvas" aria-label="Call to action placement canvas">
        <div id="bb-cta-widget" class="widget" tabindex="0" role="button" style=${style} @pointerdown=${(e: PointerEvent) => this.#pointerDown(e)} @pointermove=${(e: PointerEvent) => this.#pointerMove(e)} @pointerup=${(e: PointerEvent) => this.#pointerUp(e)} @pointercancel=${(e: PointerEvent) => this.#pointerUp(e)} @keydown=${(e: KeyboardEvent) => this.#keyDown(e)}>
          <span class="widget-text">${c.backgroundImageId ? `Image #${c.backgroundImageId} ${text}` : text}</span>
          <span class="handle" aria-hidden="true"></span>
        </div>
      </div>
      <p class="hint">Drag to position. Drag the corner to resize. Arrow keys nudge by 1%, with Shift by 5%. Styling stays in the form below.</p>
    </div>`;
  }

  #renderForm() {
    const c = this._cfg;
    const form = c.ctaType === "form";
    return html`<div class="form">
      <fieldset class="group">
        <legend>${this.#t("bluebillywig_ctaType")}</legend>
        <label class="radio"><input type="radio" name="ctaType" id="bb-cta-type-link" .checked=${!form} @change=${() => this.#set({ ctaType: "link" })} /> ${this.#t("bluebillywig_ctaTypeLink")}</label>
        <label class="radio"><input type="radio" name="ctaType" id="bb-cta-type-form" .checked=${form} @change=${() => this.#set({ ctaType: "form" })} /> ${this.#t("bluebillywig_ctaTypeForm")}</label>
      </fieldset>

      ${this.#text(this.#t("bluebillywig_ctaTitle"), "bb-cta-title", c.title, (v) => this.#set({ title: v }), { maxlength: 255 })}

      ${form
        ? html`<div class="group">
            <p class="hint">Visitors enter an email to dismiss the overlay. The visitor's browser remembers the email address and the time of the submission (the <code>bbtl_</code> localStorage keys) for up to a year, so the form is not shown again. Unless you set a submit endpoint below, the email goes nowhere else: there is no server-side lead collection and no CRM sync.</p>
            ${this.#text(this.#t("bluebillywig_ctaEmailPlaceholder"), "bb-cta-form-email-label", c.formEmailLabel, (v) => this.#set({ formEmailLabel: v }))}
            ${this.#text(this.#t("bluebillywig_ctaSubmitLabel"), "bb-cta-form-submit-label", c.formSubmitLabel, (v) => this.#set({ formSubmitLabel: v }))}
            ${this.#text(this.#t("bluebillywig_ctaThankYou"), "bb-cta-form-thank-you", c.formThankYou, (v) => this.#set({ formThankYou: v }))}
            ${this.#text(this.#t("bluebillywig_ctaEndpoint"), "bb-cta-form-endpoint", c.formSubmitEndpoint, (v) => this.#set({ formSubmitEndpoint: v }), { type: "url", placeholder: "https://your-site.example/bb-cta", maxlength: 2048 })}
            <p class="hint">If set, the visitor's browser POSTs <code>{"{email, clipId, consent, submittedAt}"}</code> as JSON to this URL, so the email address is sent to whoever runs that endpoint. It must be https, and accept <code>application/json</code> from your site with CORS and answer 2xx. A plain http URL works only on localhost, for testing, and only on a page that is itself on localhost. Leave it empty to send nothing.</p>
          </div>`
        : html`<div class="group">
            <label class="field">Headline (optional)
              <uui-input id="bb-cta-headline" label="Headline" maxlength="500" .value=${c.headline} @input=${(e: Event) => this.#set({ headline: (e.target as HTMLInputElement).value })}></uui-input>
            </label>
            <label class="field">Body (optional)
              <uui-textarea id="bb-cta-body" label="Body" maxlength="2000" .value=${c.body} @input=${(e: Event) => this.#set({ body: (e.target as HTMLTextAreaElement).value })}></uui-textarea>
            </label>
            ${this.#text(this.#t("bluebillywig_ctaButtonLabel"), "bb-cta-button-label", c.buttonLabel, (v) => this.#set({ buttonLabel: v }))}
            ${this.#text(this.#t("bluebillywig_ctaTargetUrl"), "bb-cta-url", c.url, (v) => this.#set({ url: v }), { type: "url", placeholder: "https://", maxlength: 2048 })}
            <uui-checkbox id="bb-cta-new-tab" label=${this.#t("bluebillywig_ctaNewTab")} ?checked=${c.openInNewTab} @change=${(e: Event) => this.#set({ openInNewTab: (e.target as HTMLInputElement).checked })}>${this.#t("bluebillywig_ctaNewTab")}</uui-checkbox>
          </div>`}

      <uui-checkbox id="bb-cta-pause" label=${this.#t("bluebillywig_ctaPause")} ?checked=${c.pauseOnShow} @change=${(e: Event) => this.#set({ pauseOnShow: (e.target as HTMLInputElement).checked })}>${this.#t("bluebillywig_ctaPause")}</uui-checkbox>

      <details class="group" ?open=${!!c.backgroundImageId}>
        <summary>Background image</summary>
        <p class="hint">Use an image as the call to action instead of text on a colour. The text above is then laid over the image.</p>
        <div class="row">
          <input id="bb-cta-image-id" type="text" inputmode="numeric" placeholder="Image ID" autocomplete="off" .value=${c.backgroundImageId} @input=${(e: Event) => this.#set({ backgroundImageId: (e.target as HTMLInputElement).value.trim() })} />
          <uui-button id="bb-cta-image-clear" look="secondary" label="Clear" @click=${() => this.#set({ backgroundImageId: "" })}>Clear</uui-button>
        </div>
        <div class="row">
          <label class="file">Upload image (JPG, PNG, GIF or WebP)
            <input id="bb-cta-image-file" type="file" accept="image/jpeg,image/png,image/gif,image/webp" @change=${(e: Event) => void this.#uploadImage(e)} />
          </label>
          <span id="bb-cta-image-status" class="status" role="status" aria-live="polite">${this._imageStatus ?? ""}</span>
        </div>
        <label class="field">Or search existing images
          <input id="bb-cta-image-search" type="text" autocomplete="off" @input=${(e: Event) => this.#searchImages((e.target as HTMLInputElement).value)} />
        </label>
        <div class="results" id="bb-cta-image-results">
          ${this._imageResults.map((r) => html`<button type="button" class="result" data-clip-id=${r.id} title=${`${r.title} (#${r.id})`} @click=${() => { this.#set({ backgroundImageId: String(r.id) }); this._imageResults = []; }}>
            ${r.thumbnailUrl ? html`<img src=${r.thumbnailUrl} alt="" />` : nothing}<span>#${r.id}</span>
          </button>`)}
          ${this._imageSearched && this._imageResults.length === 0 ? html`<span class="hint">No images found</span>` : nothing}
        </div>
      </details>

      <fieldset class="group">
        <legend>${this.#t("bluebillywig_ctaTiming")}</legend>
        <div class="row">
          ${this.#number(this.#t("bluebillywig_ctaShowFrom"), "bb-cta-start", c.startAt, (v) => this.#set({ startAt: v }), { min: 0, step: 0.1 })}
          ${this.#number(this.#t("bluebillywig_ctaShowUntil"), "bb-cta-end", c.endAt, (v) => this.#set({ endAt: v }), { min: 0, step: 0.1 })}
        </div>
        <p class="hint">${this._clipLength > 0 ? `This clip is ${this._clipLength} seconds long.` : "Blue Billywig has not reported this clip's length yet, so \"until the end\" cannot be saved."}</p>
      </fieldset>

      ${this._mode === "simple"
        ? html`<fieldset class="group">
            <legend>Position preset</legend>
            <select id="bb-cta-preset" @change=${(e: Event) => this.#pickPreset((e.target as HTMLSelectElement).value)}>
              ${Object.entries(PRESETS).map(([name, p]) => html`<option value=${name} ?selected=${this._preset === name}>${p.label}</option>`)}
              <option value="custom" ?selected=${this._preset === "custom"}>Custom (edit in Canvas)</option>
            </select>
          </fieldset>`
        : nothing}

      <fieldset class="group">
        <legend>Styling</legend>
        <div class="row">
          ${this.#color("Background", "bb-cta-bg", c.backgroundColor, (v) => this.#set({ backgroundColor: v }))}
          ${this.#color("Hover", "bb-cta-hover", c.hoverColor, (v) => this.#set({ hoverColor: v }))}
          ${this.#color("Text", "bb-cta-text-color", c.textColor, (v) => this.#set({ textColor: v }))}
        </div>
        <div class="row">
          ${this.#range("Font size (% of canvas)", "bb-cta-font-size", c.fontSize, 1.5, 8, 0.1, (v) => this.#set({ fontSize: v }))}
          <label class="field">Font
            <select id="bb-cta-font" @change=${(e: Event) => this.#set({ fontFamily: (e.target as HTMLSelectElement).value })}>
              ${(FONTS.includes(c.fontFamily) ? FONTS : [...FONTS, c.fontFamily]).map((f) => html`<option value=${f} ?selected=${c.fontFamily === f}>${f}</option>`)}
            </select>
          </label>
        </div>
        <div class="row">
          ${this.#range("Corner radius (%)", "bb-cta-radius", c.cornerRadius, 0, 10, 0.1, (v) => this.#set({ cornerRadius: v }))}
          ${this.#number("Border (px)", "bb-cta-border", c.borderThickness, (v) => this.#set({ borderThickness: Math.round(v) }), { min: 0, max: 12, step: 1 })}
          ${this.#color("Border color", "bb-cta-border-color", c.borderColor, (v) => this.#set({ borderColor: v }))}
        </div>
        ${this.#range("Background opacity (%)", "bb-cta-alpha", c.backgroundAlpha, 0, 100, 1, (v) => this.#set({ backgroundAlpha: v }))}
        <div class="row">
          ${this.#number("Left (%)", "bb-cta-x", c.xpos, (v) => this.#setPosition(v, c.ypos, c.width, c.height), { min: 0, max: 100, step: 0.5 })}
          ${this.#number("Top (%)", "bb-cta-y", c.ypos, (v) => this.#setPosition(c.xpos, v, c.width, c.height), { min: 0, max: 100, step: 0.5 })}
          ${this.#number("Width (%)", "bb-cta-w", c.width, (v) => this.#setPosition(c.xpos, c.ypos, v, c.height), { min: 1, max: 100, step: 0.5 })}
          ${this.#number("Height (%)", "bb-cta-h", c.height, (v) => this.#setPosition(c.xpos, c.ypos, c.width, v), { min: 1, max: 100, step: 0.5 })}
        </div>
      </fieldset>
    </div>`;
  }

  override render() {
    if (!this._enabled) return nothing;
    const foreign = !!this._foreignId;
    const modes: [Mode, string][] = [["off", this.#t("bluebillywig_ctaModeOff")], ["simple", this.#t("bluebillywig_ctaModeSimple")], ["canvas", this.#t("bluebillywig_ctaModeCanvas")]];
    return html`<uui-box headline=${this.#t("bluebillywig_ctaHeadline")} id="bb-cta-section">
      ${this._loadError ? html`<p class="error" id="bb-cta-load-error" role="alert">${this._loadError}</p>` : nothing}
      ${this._loaded
        ? html`<div class="tabs" role="tablist" aria-label=${this.#t("bluebillywig_ctaModeLabel")}>
              ${modes.map(([m, label]) => html`<uui-button id=${`bb-cta-mode-${m}`} role="tab" aria-selected=${this._mode === m} tabindex=${this._mode === m ? "0" : "-1"} @keydown=${(e: KeyboardEvent) => this.#tabKey(e, modes)} look=${this._mode === m ? "primary" : "secondary"} compact label=${label} ?disabled=${foreign} @click=${() => this.#setMode(m)}>${label}</uui-button>`)}
            </div>
            ${this._mode === "off" ? html`<p class="hint" id="bb-cta-off-hint">${foreign ? "" : this.#t(this._timelineId ? "bluebillywig_ctaOffRemoves" : "bluebillywig_ctaNone")}</p>` : nothing}
            ${this._mode === "canvas" ? this.#renderCanvas() : nothing}
            ${this._mode !== "off" ? html`<fieldset class="locked" ?disabled=${foreign}>${this.#renderForm()}</fieldset>` : nothing}
            ${this._errors.length > 0 && this._mode !== "off"
              ? html`<ul class="errors" id="bb-cta-errors" role="alert">${this._errors.map((m) => html`<li>${m}</li>`)}</ul>`
              : nothing}
            <div class="footer">
              <uui-button id="bb-cta-save" look="primary" color="positive" label=${this.#t("bluebillywig_ctaSave")} ?disabled=${foreign || this._busy || (this._mode === "off" && !this._timelineId) || (this._mode !== "off" && this._errors.length > 0)} @click=${() => void this.#save()}>${this._mode === "off" ? this.#t("bluebillywig_ctaRemove") : this.#t("bluebillywig_ctaSave")}</uui-button>
              ${this._timelineId && this._mode !== "off" && !foreign ? html`<uui-button id="bb-cta-delete" look="secondary" color="danger" label=${this.#t("bluebillywig_ctaRemove")} ?disabled=${this._busy} @click=${() => void this.#delete()}>${this.#t("bluebillywig_ctaRemove")}</uui-button>` : nothing}
              <span id="bb-cta-status" class=${this._status?.kind === "error" ? "error" : "ok"} role="status" aria-live="polite">${this._status?.text ?? ""}</span>
            </div>`
        : this._loadError
          ? nothing
          : html`<uui-loader-bar></uui-loader-bar>`}
    </uui-box>`;
  }

  static override styles = css`
    :host { display: block; }
    uui-box { margin-bottom: var(--uui-size-space-4); }
    .tabs { display: flex; gap: var(--uui-size-space-2); margin-bottom: var(--uui-size-space-4); }
    .form { display: grid; gap: var(--uui-size-space-4); }
    .group { display: grid; gap: var(--uui-size-space-3); border: 1px solid var(--uui-color-border); border-radius: var(--uui-border-radius); padding: var(--uui-size-space-4); margin: 0; min-width: 0; }
    .locked { border: 0; padding: 0; margin: 0; min-width: 0; }
    .field { display: grid; gap: var(--uui-size-space-1); font-weight: 600; font-size: 13px; }
    .field input[type="number"], .field select, .group select, input[type="text"] { font: inherit; font-weight: normal; padding: 4px 6px; }
    .field input[type="color"] { width: 64px; height: 32px; padding: 0; }
    .field uui-input, .field uui-textarea { font-weight: normal; }
    .radio { display: flex; gap: var(--uui-size-space-2); align-items: center; }
    .row { display: flex; flex-wrap: wrap; gap: var(--uui-size-space-4); align-items: flex-end; }
    .hint { color: var(--uui-color-text-alt); font-size: 12px; margin: var(--uui-size-space-2) 0 0; }
    .error { color: var(--uui-color-danger-standalone); }
    .ok { color: var(--uui-color-positive-standalone); }
    .errors { color: var(--uui-color-danger-standalone); margin: var(--uui-size-space-3) 0 0; padding-left: var(--uui-size-space-5); }
    .footer { display: flex; align-items: center; gap: var(--uui-size-space-3); margin-top: var(--uui-size-space-4); flex-wrap: wrap; }
    .status { color: var(--uui-color-text-alt); }
    .file { display: grid; gap: var(--uui-size-space-1); font-weight: 600; font-size: 13px; }
    .file input { font-weight: normal; }
    .results { display: flex; flex-wrap: wrap; gap: var(--uui-size-space-2); }
    .result { display: grid; justify-items: center; gap: 2px; border: 1px solid var(--uui-color-border); background: transparent; border-radius: var(--uui-border-radius); padding: 4px; cursor: pointer; color: inherit; }
    .result img { width: 72px; height: 48px; object-fit: cover; }
    .canvas-wrap { margin-bottom: var(--uui-size-space-4); }
    .canvas { position: relative; width: 100%; aspect-ratio: 16 / 9; background: #111 linear-gradient(135deg, #222, #000); border-radius: var(--uui-border-radius); overflow: hidden; touch-action: none; }
    .widget { position: absolute; box-sizing: border-box; display: flex; align-items: center; justify-content: center; text-align: center; cursor: move; user-select: none; overflow: hidden; outline-offset: 2px; }
    .widget:focus-visible { outline: 2px solid var(--uui-color-selected); }
    .widget-text { pointer-events: none; padding: 0 4px; }
    .handle { position: absolute; right: 0; bottom: 0; width: 14px; height: 14px; background: #fff; border: 2px solid #000; cursor: nwse-resize; box-sizing: border-box; }
  `;
}

export default BbCtaEditorElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-cta-editor": BbCtaEditorElement;
  }
}
