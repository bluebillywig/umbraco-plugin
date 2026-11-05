import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getStatus } from "../api/index.js";
import { isSettingsPath, SETTINGS_PATH, shouldShowNotice } from "./credentials-notice.js";

/**
 * The backoffice-wide "not configured yet" notice, the counterpart of the reference plugin's admin notice: shown in every
 * section (a header app is part of the backoffice frame), to administrators only (the manifest's condition), not on the
 * package's own settings screen (the form there is the notice's resolution), and dismissible for the current page load only (kept in memory, nothing is stored in the browser). It shows nothing when the package is configured and nothing when the status cannot be read.
 */
@customElement("bb-credentials-header-app")
export class BbCredentialsHeaderAppElement extends UmbElementMixin(LitElement) {
  @state() private _configured?: boolean;
  @state() private _onSettings = false;
  @state() private _dismissed = false;

  override connectedCallback() {
    super.connectedCallback();
    this._onSettings = isSettingsPath(window.location.pathname);
    window.addEventListener("changestate", this.#onRoute);
    window.addEventListener("navigationsuccess", this.#onRoute);
    window.addEventListener("popstate", this.#onRoute);
    void this.#loadStatus();
  }

  override disconnectedCallback() {
    super.disconnectedCallback();
    window.removeEventListener("changestate", this.#onRoute);
    window.removeEventListener("navigationsuccess", this.#onRoute);
    window.removeEventListener("popstate", this.#onRoute);
  }

  // Credentials are entered on the settings screen, so the status is read again when the user leaves it.
  #onRoute = () => {
    const was = this._onSettings;
    this._onSettings = isSettingsPath(window.location.pathname);
    if (was && !this._onSettings) void this.#loadStatus();
  };

  async #loadStatus() {
    const { data } = await tryExecute(this, getStatus(), { disableNotifications: true });
    this._configured = data?.configured;
  }

  #dismiss() {
    this._dismissed = true; // this page load only: a reload shows the notice again
  }

  override render() {
    if (!shouldShowNotice({ configured: this._configured, onSettings: this._onSettings, dismissed: this._dismissed })) return nothing;
    return html`<div id="bb-credentials-header" role="status">
      <uui-icon name="icon-alert"></uui-icon>
      <span><strong>Blue Billywig:</strong> ${this.localize.term("bluebillywig_headerNotConfigured")}</span>
      <a id="bb-credentials-header-link" href=${SETTINGS_PATH}>${this.localize.term("bluebillywig_addCredentials")}</a>
      <uui-button id="bb-credentials-header-dismiss" compact look="default" label=${this.localize.term("bluebillywig_dismissNotice")} @click=${() => this.#dismiss()}>
        <uui-icon name="icon-wrong"></uui-icon>
      </uui-button>
    </div>`;
  }

  static override styles = css`
    :host { display: flex; align-items: center; }
    div {
      display: flex;
      align-items: center;
      gap: var(--uui-size-space-3);
      padding: var(--uui-size-space-1) var(--uui-size-space-3);
      margin-right: var(--uui-size-space-3);
      border-radius: var(--uui-border-radius);
      background: var(--uui-color-warning-emphasis, #fbd142);
      color: var(--uui-color-warning-contrast, #000);
      font-size: 13px;
      white-space: nowrap;
    }
    a { color: inherit; font-weight: 700; }
  `;
}

export default BbCredentialsHeaderAppElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-credentials-header-app": BbCredentialsHeaderAppElement;
  }
}
