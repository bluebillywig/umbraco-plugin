import { css, customElement, html } from "@umbraco-cms/backoffice/external/lit";
import { UmbModalBaseElement } from "@umbraco-cms/backoffice/modal";
import type { LibraryClipResponseModel } from "../api/index.js";
import type { LibraryPickerModalValue } from "./modal-tokens.js";
import "./clip-browser.element.js";

/**
 * A media-picker style modal over the library, for the video property editor (parity E5: the reference plugin's Media
 * Library "Blue Billywig" tab). Click a clip to choose it. It reads through the content-section endpoints.
 */
@customElement("bb-library-picker-modal")
export class BbLibraryPickerModalElement extends UmbModalBaseElement<object, LibraryPickerModalValue> {
  #choose(clip: LibraryClipResponseModel) {
    this.value = { id: clip.id, title: clip.title, thumbnailUrl: clip.thumbnailUrl, status: clip.status, length: clip.length };
    this._submitModal();
  }

  override render() {
    return html`<umb-body-layout headline=${this.localize.term("bluebillywig_pickVideoHeadline")}>
      <bb-clip-browser variant="picker" @select-clip=${(e: CustomEvent<LibraryClipResponseModel>) => this.#choose(e.detail)}></bb-clip-browser>
      <div slot="actions"><uui-button label=${this.localize.term("bluebillywig_cancel")} @click=${this._rejectModal}>${this.localize.term("bluebillywig_cancel")}</uui-button></div>
    </umb-body-layout>`;
  }

  static override styles = css`
    :host { display: block; }
    bb-clip-browser { display: block; }
  `;
}

export default BbLibraryPickerModalElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-library-picker-modal": BbLibraryPickerModalElement;
  }
}
