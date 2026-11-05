import { css, customElement, html, LitElement } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { UMB_MODAL_MANAGER_CONTEXT } from "@umbraco-cms/backoffice/modal";
import type { LibraryClipResponseModel } from "../api/index.js";
import { BB_CLIP_DETAILS_MODAL } from "./modal-tokens.js";
import "./clip-browser.element.js";
import type { BbClipBrowserElement } from "./clip-browser.element.js";
import "../shared/credentials-banner.element.js";

/** The Livestreams section view: the same browser, fixed to source type "live" on the server. */
@customElement("bb-library-livestreams")
export class BbLibraryLivestreamsElement extends UmbElementMixin(LitElement) {
  async #open(clip: LibraryClipResponseModel) {
    const modalManager = await this.getContext(UMB_MODAL_MANAGER_CONTEXT);
    if (!modalManager) return;
    const result = await modalManager.open(this, BB_CLIP_DETAILS_MODAL, { data: { clipId: clip.id } }).onSubmit().catch(() => undefined);
    if (result?.deleted) await this.renderRoot.querySelector<BbClipBrowserElement>("bb-clip-browser")?.reload();
  }

  override render() {
    return html`<bb-credentials-banner></bb-credentials-banner><uui-box headline=${this.localize.term("bluebillywig_tabLivestreams")}>
      <bb-clip-browser variant="livestreams" @select-clip=${(e: CustomEvent<LibraryClipResponseModel>) => void this.#open(e.detail)}></bb-clip-browser>
    </uui-box>`;
  }

  static override styles = css`
    :host { display: block; padding: var(--uui-size-layout-1); }
  `;
}

export default BbLibraryLivestreamsElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-library-livestreams": BbLibraryLivestreamsElement;
  }
}
