import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getStatus, type StatusResponse } from "../api/index.js";
import { libraryError } from "./errors.js";
import "../shared/credentials-banner.element.js";

/** Links into the support portal. They do not depend on the publication. */
const SUPPORT_LINKS: Array<{ label: string; href: string; primary?: boolean; note?: string }> = [
  { label: "Support portal", href: "https://support.bluebillywig.com", primary: true, note: "Knowledge base, guides, and how-tos" },
  { label: "SAPI documentation", href: "https://support.bluebillywig.com/category/sapi/" },
  { label: "API key management", href: "https://support.bluebillywig.com/category/api-keys/" },
  { label: "Setup & implementation guide", href: "https://support.bluebillywig.com/setup-and-implementation/" },
];

/**
 * The Platform section view: the publication this site is connected to and links into its online video platform. Port of
 * the plugin's Platform page. The OVP address is built from the configured base URL on the server, so a publication on a
 * development OVP links to that OVP.
 */
@customElement("bb-library-platform")
export class BbLibraryPlatformElement extends UmbElementMixin(LitElement) {
  @state() private _status?: StatusResponse;
  @state() private _error?: string;

  override async connectedCallback() {
    super.connectedCallback();
    const { data, error } = await tryExecute(this, getStatus());
    if (error || !data) this._error = libraryError(error, "platform details");
    else this._status = data;
  }

  #link(label: string, href: string, note?: string, primary = false) {
    return html`<li>
      <a href=${href} target="_blank" rel="noopener noreferrer" class=${primary ? "primary" : ""}>${label}</a>
      ${note ? html`<span>${note}</span>` : nothing}
    </li>`;
  }

  #connected(s: StatusResponse) {
    const ovp = s.ovpUrl;
    return html`<uui-box headline="Publication">
        <p class="name" id="bb-platform-name">${s.publicationName ?? s.publication}</p>
        <p class="url" id="bb-platform-url">${ovp.replace(/\/ovp\/$/, "").replace(/^https:\/\//, "")}</p>
      </uui-box>
      <uui-box headline="Quick links">
        <ul id="bb-platform-links">
          ${this.#link("Open OVP", `${ovp}#/login`, "Manage videos, playouts, channels, and settings", true)}
          ${this.#link("Media Library", `${ovp}#/library/mediaclips`)}
          ${this.#link("Playouts", `${ovp}#/library/playout`)}
          ${this.#link("Channels", `${ovp}#/library/channels`)}
          ${this.#link("Analytics", `${ovp}#/analytics`)}
        </ul>
      </uui-box>`;
  }

  override render() {
    const s = this._status;
    return html`<bb-credentials-banner></bb-credentials-banner>
      ${this._error ? html`<uui-box><p class="error" role="alert">${this._error}</p></uui-box>` : nothing}
      ${s?.configured ? this.#connected(s) : nothing}
      <uui-box headline="Documentation & support">
        <ul id="bb-platform-support">${SUPPORT_LINKS.map((l) => this.#link(l.label, l.href, l.note, l.primary))}</ul>
      </uui-box>`;
  }

  static override styles = css`
    :host { display: block; padding: var(--uui-size-layout-1); }
    uui-box { margin-bottom: var(--uui-size-space-5); }
    ul { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--uui-size-space-3); }
    li span { margin-left: var(--uui-size-space-3); opacity: 0.7; }
    a.primary { font-weight: 700; }
    .name { font-size: 18px; font-weight: 700; margin: 0; }
    .url { margin: 0; opacity: 0.7; }
    .error { color: var(--uui-color-danger); }
  `;
}

export default BbLibraryPlatformElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-library-platform": BbLibraryPlatformElement;
  }
}
