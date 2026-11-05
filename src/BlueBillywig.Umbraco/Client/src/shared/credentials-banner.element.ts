import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getSettingsGateHealth, getStatus, type GateHealthResponseModel } from "../api/index.js";

import { SETTINGS_PATH } from "./credentials-notice.js";

export { SETTINGS_PATH };

/** Which translation key says what each reason the gate closed on means, in words an editor can act on (plugin: the gate error notice). Unknown reasons are shown as they are. */
const GATE_REASON_KEYS: Record<string, string> = {
  sapi_disconnected: "bluebillywig_reasonSapiDisconnected",
  sapi_error: "bluebillywig_reasonSapiError",
  clip_not_found: "bluebillywig_reasonClipNotFound",
  channel_not_found: "bluebillywig_reasonChannelNotFound",
  secret_missing: "bluebillywig_reasonSecretMissing",
  salt_missing: "bluebillywig_reasonSaltMissing",
  invalid_entity: "bluebillywig_reasonInvalidEntity",
  inject_failed: "bluebillywig_reasonInjectFailed",
  internal_error: "bluebillywig_reasonInternalError",
  cache_unsafe: "bluebillywig_reasonCacheUnsafe",
};

/**
 * The banner for a package without credentials, the counterpart of the plugin's "installed but not configured yet"
 * admin notice. It shows nothing when the package is configured, and nothing when the status cannot be read: a banner that
 * guesses is worse than none. The link goes to the settings dashboard, which only users with access to the Settings section can open.
 */
@customElement("bb-credentials-banner")
export class BbCredentialsBannerElement extends UmbElementMixin(LitElement) {
  @state() private _configured?: boolean;
  @state() private _gate?: GateHealthResponseModel;

  override async connectedCallback() {
    super.connectedCallback();
    const { data } = await tryExecute(this, getStatus());
    this._configured = data?.configured;
    // Content protection health needs the Settings section; for anyone else the request is refused and no banner shows (the
    // plugin shows its gate error notice to administrators only). A failed read shows nothing: a banner that guesses is worse than none.
    const health = await tryExecute(this, getSettingsGateHealth(), { disableNotifications: true });
    this._gate = health.data;
  }

  override render() {
    return html`${this.#credentials()}${this.#gate()}`;
  }

  #credentials() {
    if (this._configured !== false) return nothing;
    return html`<div id="bb-credentials-banner" role="alert">
      <strong>Blue Billywig:</strong> ${this.localize.term("bluebillywig_bannerNotConfigured")}
      <a id="bb-credentials-banner-link" href=${SETTINGS_PATH}>${this.localize.term("bluebillywig_addCredentials")}</a>
    </div>`;
  }

  /** G7: protected videos were replaced by the gate message because the gate could not decide (the last hour), and the pepper failure. */
  #gate() {
    const g = this._gate;
    if (!g || g.healthy) return nothing;
    const reasons = g.errors.map((e) => `${GATE_REASON_KEYS[e.reason] ? this.localize.term(GATE_REASON_KEYS[e.reason]) : e.reason} (${e.count})`).join("; ");
    return html`${g.errorTotal > 0
        ? html`<div id="bb-gate-banner" role="alert">
            <strong>${this.localize.term("bluebillywig_gateBannerTitle")}</strong>
            ${this.localize.term(g.errorTotal === 1 ? "bluebillywig_gateBannerOne" : "bluebillywig_gateBannerMany", g.errorTotal, reasons)}
            <a id="bb-gate-banner-link" href=${SETTINGS_PATH}>${this.localize.term("bluebillywig_openSettings")}</a>
          </div>`
        : nothing}
      ${g.pepperFailed
        ? html`<div id="bb-pepper-banner" role="alert">
            <strong>${this.localize.term("bluebillywig_gateBannerTitle")}</strong>
            ${this.localize.term("bluebillywig_pepperBanner")}
          </div>`
        : nothing}`;
  }

  static override styles = css`
    :host { display: block; }
    div {
      margin-bottom: var(--uui-size-space-5);
      padding: var(--uui-size-space-4) var(--uui-size-space-5);
      border-left: 4px solid var(--uui-color-warning);
      background: var(--uui-color-warning-emphasis, #fff3cd);
      color: var(--uui-color-warning-contrast, #000);
      border-radius: var(--uui-border-radius);
    }
    a { color: inherit; font-weight: 700; }
    #bb-gate-banner, #bb-pepper-banner { border-left-color: var(--uui-color-danger); background: var(--uui-color-danger-emphasis, #f8d7da); color: var(--uui-color-danger-contrast, #000); }
  `;
}

export default BbCredentialsBannerElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-credentials-banner": BbCredentialsBannerElement;
  }
}
