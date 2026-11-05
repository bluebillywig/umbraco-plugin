import { css, customElement, html, LitElement, nothing, property } from "@umbraco-cms/backoffice/external/lit";
import type { LibraryClipResponseModel } from "../api/index.js";
import { formatDuration } from "./errors.js";
import { hoverPreviewAllowed } from "../shared/accessibility.js";

const HOVER_DELAY_MS = 300;

/**
 * One clip in the grid. Ports bb-hover-preview.js: after a short hover the still thumbnail is covered by a silent,
 * looping moving thumbnail. It is skipped for touch devices and for people who asked for reduced motion.
 */
@customElement("bb-clip-tile")
export class BbClipTileElement extends LitElement {
  @property({ attribute: false }) clip?: LibraryClipResponseModel;

  /** Shows a checkbox for bulk actions. */
  @property({ type: Boolean }) selectable = false;
  @property({ type: Boolean }) selected = false;
  /** True when the selection is full: an unselected clip cannot be added. */
  @property({ type: Boolean }) selectionFull = false;

  #timer?: ReturnType<typeof setTimeout>;
  #video?: HTMLVideoElement;

  #hoverAllowed() {
    return hoverPreviewAllowed((query) => window.matchMedia(query).matches);
  }

  #enter() {
    const url = this.clip?.previewUrl;
    if (!url || !this.#hoverAllowed()) return;
    clearTimeout(this.#timer);
    this.#timer = setTimeout(() => this.#showPreview(url), HOVER_DELAY_MS);
  }

  #leave() {
    clearTimeout(this.#timer);
    this.#video?.remove();
    this.#video = undefined;
    const img = this.renderRoot.querySelector<HTMLElement>("img.still");
    if (img) img.style.visibility = "";
  }

  #showPreview(url: string) {
    const box = this.renderRoot.querySelector<HTMLElement>(".thumb");
    if (!box || this.#video) return;
    const video = document.createElement("video");
    video.className = "moving";
    video.muted = true;
    video.loop = true;
    video.autoplay = true;
    video.playsInline = true;
    video.preload = "none";
    video.src = url;
    this.#video = video;
    box.append(video);
    video.play().catch(() => {
      // Autoplay can be refused; the still image stays visible then.
      this.#leave();
    });
    video.addEventListener("playing", () => {
      const img = this.renderRoot.querySelector<HTMLElement>("img.still");
      if (img) img.style.visibility = "hidden";
    });
  }

  override disconnectedCallback() {
    super.disconnectedCallback();
    this.#leave();
  }

  #open() {
    if (this.clip) this.dispatchEvent(new CustomEvent("open-clip", { detail: this.clip, bubbles: true, composed: true }));
  }

  #toggle(e: Event) {
    e.stopPropagation();
    if (this.clip) this.dispatchEvent(new CustomEvent("toggle-select", { detail: this.clip, bubbles: true, composed: true }));
  }

  override render() {
    const clip = this.clip;
    if (!clip) return nothing;
    const duration = formatDuration(clip.length);
    return html`<div
      class=${this.selected ? "tile is-selected" : "tile"}
      role="button"
      tabindex="0"
      data-clip-id=${clip.id}
      data-has-preview=${clip.previewUrl ? "1" : "0"}
      aria-label=${clip.title}
      @mouseenter=${() => this.#enter()}
      @mouseleave=${() => this.#leave()}
      @focus=${() => this.#enter()}
      @blur=${() => this.#leave()}
      @click=${() => this.#open()}
      @keydown=${(e: KeyboardEvent) => {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          this.#open();
        }
      }}>
      <div class="thumb">
        ${clip.thumbnailUrl
          ? html`<img class="still" src=${clip.thumbnailUrl} alt="" loading="lazy" />`
          : html`<span class="none"><uui-icon name="icon-movie-alt"></uui-icon></span>`}
        ${duration ? html`<span class="duration">${duration}</span>` : nothing}
        ${this.selectable
          ? html`<input
              type="checkbox"
              class="select"
              aria-label=${`Select ${clip.title}`}
              .checked=${this.selected}
              ?disabled=${this.selectionFull && !this.selected}
              @click=${(e: Event) => this.#toggle(e)}
              @keydown=${(e: KeyboardEvent) => e.stopPropagation()} />`
          : nothing}
      </div>
      <div class="info">
        <div class="title" title=${clip.title}>${clip.title}</div>
        <div class="meta">
          ${clip.mediaType ?? ""}${clip.status && clip.status !== "published" ? html` <uui-tag look="secondary" compact>${clip.status}</uui-tag>` : nothing}
        </div>
      </div>
    </div>`;
  }

  static override styles = css`
    :host { display: block; }
    .tile { cursor: pointer; border: 2px solid transparent; border-radius: var(--uui-border-radius); background: var(--uui-color-surface); box-shadow: var(--uui-shadow-depth-1); overflow: hidden; }
    .tile:hover, .tile:focus-visible { border-color: var(--uui-color-selected); outline: none; }
    .thumb { position: relative; aspect-ratio: 16 / 9; background: #000; }
    .thumb img, .thumb video { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
    .none { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; color: #999; font-size: 2rem; }
    .duration { position: absolute; right: 6px; bottom: 6px; padding: 1px 5px; border-radius: 3px; background: rgba(0, 0, 0, 0.7); color: #fff; font-size: 11px; }
    .select { position: absolute; left: 6px; top: 6px; width: 18px; height: 18px; z-index: 2; cursor: pointer; }
    .tile.is-selected { border-color: var(--uui-color-selected); }
    .info { padding: var(--uui-size-space-3); }
    .title { font-weight: 600; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .meta { color: var(--uui-color-text-alt); font-size: 12px; margin-top: 2px; min-height: 18px; }
  `;
}

export default BbClipTileElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-clip-tile": BbClipTileElement;
  }
}
