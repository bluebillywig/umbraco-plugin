import { css, customElement, html, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { LitElement } from "@umbraco-cms/backoffice/external/lit";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { umbConfirmModal } from "@umbraco-cms/backoffice/modal";
import {
  getSettings,
  getSettingsGate,
  getSettingsGateHealth,
  getSettingsGateMemberGroups,
  getSettingsGatePolicies,
  getSettingsPlayouts,
  postSettingsCleanup,
  postSettingsClearCache,
  postSettingsDisconnect,
  postSettingsTestConnection,
  putSettings,
  putSettingsGate,
  type GateHealthResponseModel,
  type GateSettingsResponseModel,
  type PlayoutOptionResponseModel,
  type PolicyResponseModel,
  type SettingsRequest,
  type SettingsResponse,
} from "../api/index.js";
import { defaultApiAddress, errorText, saveError } from "../library/errors.js";
import type { Term } from "../localization/terms.js";

const EMBED_TYPES = ["javascript", "iframe", "oembed", "url", "amp"];

// The form edits the response shape; secrets are write-only, so they only ever exist here as typed input.
type SettingsForm = SettingsResponse & { apiSecret?: string; readOnlyApiSecret?: string };
type TextField = "publication" | "apiId" | "apiSecret" | "readOnlyApiId" | "readOnlyApiSecret" | "pageCount";

@customElement("bb-settings-dashboard")
export class BbSettingsDashboardElement extends UmbElementMixin(LitElement) {
  #t: Term = (key, ...args) => this.localize.term(key, ...args);

  @state() private _settings?: SettingsForm;
  @state() private _message?: string;
  @state() private _error?: string;
  @state() private _busy = false;
  @state() private _playouts: PlayoutOptionResponseModel[] = [];
  @state() private _playoutError?: string;
  // Content protection. The shared secret is write-only: it only ever exists here as typed input.
  @state() private _gate?: GateSettingsResponseModel;
  @state() private _gateSecret = "";
  @state() private _gateMessage?: string;
  @state() private _gateError?: string;
  @state() private _gateGroups: string[] = [];
  @state() private _policies: PolicyResponseModel[] = [];
  @state() private _policyError?: string;
  @state() private _gateHealth?: GateHealthResponseModel;

  override async connectedCallback() {
    super.connectedCallback();
    const { data, error } = await tryExecute(this, getSettings(), { disableNotifications: true });
    if (error || !data) {
      this._error = errorText(error) ?? this.#t("bluebillywig_settingsLoadFailed");
      return;
    }
    this._settings = data;
    void this.#loadPlayouts();
    void this.#loadGate();
  }

  async #loadGate() {
    const { data } = await tryExecute(this, getSettingsGate());
    if (data) this._gate = data;
    const groups = await tryExecute(this, getSettingsGateMemberGroups(), { disableNotifications: true });
    this._gateGroups = groups.data ?? [];
    const health = await tryExecute(this, getSettingsGateHealth(), { disableNotifications: true });
    this._gateHealth = health.data;
    this._policyError = undefined;
    if (this._settings?.hasApiSecret && this._settings.apiId && this._settings.publication) {
      const policies = await tryExecute(this, getSettingsGatePolicies(), { disableNotifications: true });
      if (policies.error || !policies.data) {
        this._policies = [];
        this._policyError = this.#t("bluebillywig_policiesLoadFailed");
      } else {
        this._policies = policies.data;
      }
    }
  }

  async #saveGate() {
    if (!this._gate) return;
    this._busy = true;
    this._gateMessage = this._gateError = undefined;
    const body = {
      enabled: this._gate.enabled,
      expirySeconds: this._gate.expirySeconds,
      memberGroups: this._gate.memberGroups,
      message: this._gate.message ?? "",
      loginUrl: this._gate.loginUrl ?? "",
      sharedSecret: this._gateSecret.length > 0 ? this._gateSecret : undefined,
      clearSharedSecret: false,
    };
    const { data, error } = await tryExecute(this, putSettingsGate({ body }), { disableNotifications: true });
    if (error || !data) {
      this._gateError = errorText(error) ?? this.#t("bluebillywig_gateSaveFailed");
    } else {
      this._gate = data;
      this._gateSecret = "";
      this._gateMessage = this.#t("bluebillywig_gateSaved");
    }
    this._busy = false;
  }

  async #clearGateSecret() {
    if (!this._gate) return;
    if (!(await this.#confirm(this.#t("bluebillywig_removeSecretHeadline"), this.#t("bluebillywig_removeSecretContent"), this.#t("bluebillywig_removeSecret"), "danger"))) return;
    this._busy = true;
    this._gateMessage = this._gateError = undefined;
    const { data, error } = await tryExecute(this, putSettingsGate({ body: { enabled: this._gate.enabled, expirySeconds: this._gate.expirySeconds, memberGroups: this._gate.memberGroups, message: this._gate.message ?? "", loginUrl: this._gate.loginUrl ?? "", clearSharedSecret: true } }), { disableNotifications: true });
    if (error || !data) this._gateError = errorText(error) ?? this.#t("bluebillywig_secretRemoveFailed");
    else {
      this._gate = data;
      this._gateMessage = this.#t("bluebillywig_secretRemoved");
    }
    this._busy = false;
  }

  #setGate<K extends keyof GateSettingsResponseModel>(field: K, value: GateSettingsResponseModel[K]) {
    if (this._gate) this._gate = { ...this._gate, [field]: value };
  }

  #toggleGroup(name: string, on: boolean) {
    if (!this._gate) return;
    const next = on ? [...new Set([...this._gate.memberGroups, name])] : this._gate.memberGroups.filter((g) => g !== name);
    this.#setGate("memberGroups", next);
  }

  #gateBox() {
    const g = this._gate;
    if (!g) return nothing;
    const groups = [...new Set([...this._gateGroups, ...g.memberGroups])];
    const h = this._gateHealth;
    return html`<uui-box headline=${this.#t("bluebillywig_gateHeadline")}>
      <p class="hint">
        Clips and channels with a content protection policy get a signed token for members of the groups below; everybody else sees the gate message.
        Create the policy in Blue Billywig with a "Shared Secret" rule and paste its secret here. A backoffice login is not a member: to watch protected videos, sign in as a member.
      </p>
      ${h && !h.healthy
        ? html`<p class="error" id="bb-settings-gate-health">
            ${h.errorTotal > 0 ? html`${h.errorTotal} protected video${h.errorTotal === 1 ? "" : "s"} could not be checked in the last hour: ${h.errors.map((e) => `${e.reason} (${e.count})`).join(", ")}. ` : nothing}
            ${h.pepperFailed ? html`The token pepper could not be saved.` : nothing}
          </p>`
        : nothing}
      <umb-property-layout label=${this.#t("bluebillywig_gateEnable")}>
        <uui-toggle slot="editor" id="bb-gate-enabled" label=${this.#t("bluebillywig_gateEnable")} .checked=${g.enabled} @change=${(e: Event) => this.#setGate("enabled", (e.target as HTMLInputElement).checked)}></uui-toggle>
      </umb-property-layout>
      <umb-property-layout label=${this.#t("bluebillywig_gateSharedSecret")} description=${g.sharedSecretPinned ? "Set in configuration (BlueBillywig:GateSharedSecret); it cannot be changed here." : this.#t("bluebillywig_gateSharedSecretHint")}>
        <uui-input
          slot="editor"
          id="bb-gate-secret"
          label=${this.#t("bluebillywig_gateSharedSecret")}
          type="password"
          autocomplete="off"
          ?readonly=${g.sharedSecretPinned}
          placeholder=${g.hasSharedSecret ? this.#t("bluebillywig_secretSaved") : ""}
          .value=${this._gateSecret}
          @input=${(e: Event) => (this._gateSecret = (e.target as HTMLInputElement).value)}></uui-input>
        ${g.hasSharedSecret && !g.sharedSecretPinned
          ? html`<uui-button slot="editor" look="secondary" color="danger" label=${this.#t("bluebillywig_removeSecret")} ?disabled=${this._busy} @click=${() => void this.#clearGateSecret()}></uui-button>`
          : nothing}
      </umb-property-layout>
      <umb-property-layout label=${this.#t("bluebillywig_gateLifetime")} description="300 to 7200. A shared link keeps working this long.">
        <uui-input
          slot="editor"
          id="bb-gate-expiry"
          label=${this.#t("bluebillywig_gateLifetime")}
          type="number"
          min="300"
          max="7200"
          .value=${String(g.expirySeconds)}
          @input=${(e: Event) => this.#setGate("expirySeconds", Number((e.target as HTMLInputElement).value))}></uui-input>
      </umb-property-layout>
      <umb-property-layout label=${this.#t("bluebillywig_gateGroups")} description="Members of any ticked group may watch. With none ticked nobody can.">
        <div slot="editor" id="bb-gate-groups">
          ${groups.length === 0
            ? html`<span class="hint">No member groups exist yet. Create them in the Members section.</span>`
            : groups.map(
                (name) => html`<div><uui-checkbox label=${name} .checked=${g.memberGroups.includes(name)} @change=${(e: Event) => this.#toggleGroup(name, (e.target as HTMLInputElement).checked)}></uui-checkbox></div>`,
              )}
        </div>
      </umb-property-layout>
      <umb-property-layout label=${this.#t("bluebillywig_gateLoginUrl")} description="Where the gate message sends visitors who are not signed in. {returnUrl} is replaced by the current page.">
        <uui-input slot="editor" id="bb-gate-login" label=${this.#t("bluebillywig_gateLoginUrl")} placeholder="/login?returnUrl={returnUrl}" .value=${g.loginUrl ?? ""} @input=${(e: Event) => this.#setGate("loginUrl", (e.target as HTMLInputElement).value)}></uui-input>
      </umb-property-layout>
      <umb-property-layout label=${this.#t("bluebillywig_gateMessageLabel")} description="Plain text shown instead of a protected video. Empty: the default message.">
        <uui-textarea slot="editor" id="bb-gate-message" label=${this.#t("bluebillywig_gateMessageLabel")} .value=${g.message ?? ""} @input=${(e: Event) => this.#setGate("message", (e.target as HTMLTextAreaElement).value)}></uui-textarea>
      </umb-property-layout>
      <umb-property-layout label=${this.#t("bluebillywig_gatePolicies")} description=${this._policyError ?? "For reference: assign a policy to a clip in the library's edit drawer."}>
        <div slot="editor" id="bb-gate-policies">
          ${this._policies.length === 0
            ? html`<span class="hint">${this._policyError ? "" : "None found."}</span>`
            : this._policies.map((p) => html`<div><strong>${p.name}</strong> <span class="hint">${p.status} · ${p.code}</span></div>`)}
        </div>
      </umb-property-layout>
      <div class="actions">
        <uui-button id="bb-gate-save" look="primary" label=${this.#t("bluebillywig_gateSave")} ?disabled=${this._busy} @click=${() => void this.#saveGate()}></uui-button>
        ${this._gateMessage ? html`<span id="bb-gate-message-saved">${this._gateMessage}</span>` : nothing}
        ${this._gateError ? html`<span id="bb-gate-error" class="error">${this._gateError}</span>` : nothing}
      </div>
    </uui-box>`;
  }

  /** The playouts of the publication, for the default-playout dropdown. Without a connection the dropdown offers the saved value only. */
  async #loadPlayouts() {
    this._playoutError = undefined;
    if (!this._settings?.hasApiSecret || !this._settings.apiId || !this._settings.publication) {
      this._playouts = [];
      return;
    }
    const { data, error } = await tryExecute(this, getSettingsPlayouts(), { disableNotifications: true });
    if (error || !data) {
      this._playouts = [];
      this._playoutError = this.#t("bluebillywig_playoutsLoadFailed");
      return;
    }
    this._playouts = data;
  }

  #set<K extends keyof SettingsForm>(field: K, value: SettingsForm[K]) {
    if (this._settings) this._settings = { ...this._settings, [field]: value };
  }

  #pinned(key?: string) {
    return key !== undefined && (this._settings?.configOverrides ?? []).includes(key);
  }

  #text(label: string, field: TextField, overrideKey?: string, opts: { type?: "text" | "password" | "number"; placeholder?: string } = {}) {
    const value = this._settings?.[field];
    return html`<umb-property-layout .label=${label}>
      <uui-input
        slot="editor"
        label=${label}
        type=${opts.type ?? "text"}
        .value=${value === undefined || value === null ? "" : String(value)}
        placeholder=${opts.placeholder ?? ""}
        ?readonly=${this.#pinned(overrideKey)}
        @input=${(e: Event) => {
          const input = (e.target as HTMLInputElement).value;
          this.#set(field, (field === "pageCount" ? Number(input) : input) as never);
        }}></uui-input>
    </umb-property-layout>`;
  }

  /**
   * The API origin is shown, never edited: every API call sends the read-write token to it, so it can only be set by the
   * site owner in configuration (BlueBillywig:BaseUrl, on a Blue Billywig domain). Without it the publication's default
   * address is used.
   */
  #baseUrl() {
    const s = this._settings;
    const rejected = s?.baseUrlRejected === true;
    const description = this.#t(rejected ? "bluebillywig_baseUrlRejected" : "bluebillywig_baseUrlHelp");
    // The default address only once the publication is a valid one; while it is empty or being typed wrongly there is nothing to preview.
    const preview = defaultApiAddress(s?.publication);
    return html`<umb-property-layout label=${this.#t("bluebillywig_apiBaseUrl")} description=${description}>
      <uui-input slot="editor" id="bb-settings-base-url" label=${this.#t("bluebillywig_apiBaseUrl")} type="text" readonly .value=${s?.baseUrl ?? ""} placeholder=${preview}></uui-input>
    </umb-property-layout>`;
  }

  async #save() {
    if (!this._settings) return;
    this._busy = true;
    this._message = this._error = undefined;
    const body: SettingsRequest = { ...this._settings, clearApiSecret: false, clearReadOnlyApiSecret: false };
    const { data, error } = await tryExecute(this, putSettings({ body }), { disableNotifications: true });
    if (error || !data) {
      this._error = errorText(error) ?? this.#t("bluebillywig_settingsSaveFailed");
    } else {
      // The response carries no secrets, so typed secrets are dropped from the form once saved.
      this._settings = data.settings;
      if (data.cacheCleared) {
        const connected = data.settings.hasApiSecret && data.settings.apiId !== "" && data.settings.publication !== "";
        this._message = this.#t(connected ? "bluebillywig_settingsSaved" : "bluebillywig_settingsSavedNotConnected");
      } else {
        // The plugin's clear-cache notice: the settings are saved, but answers cached from the old ones may still show.
        this._error = this.#t("bluebillywig_settingsSavedCacheFailed");
      }
      void this.#loadPlayouts();
    }
    this._busy = false;
  }

  async #confirm(headline: string, content: string, confirmLabel: string, color: "warning" | "danger"): Promise<boolean> {
    try {
      await umbConfirmModal(this, { headline, content, confirmLabel, color });
      return true;
    } catch {
      return false; // cancelled
    }
  }

  async #clearCache() {
    if (!(await this.#confirm(this.#t("bluebillywig_clearCacheHeadline"), this.#t("bluebillywig_clearCacheContent"), this.#t("bluebillywig_clearCache"), "warning"))) return;
    this._busy = true;
    this._message = this._error = undefined;
    const { data, error } = await tryExecute(this, postSettingsClearCache({ body: { confirm: true } }), { disableNotifications: true });
    if (error) this._error = saveError(error, "cache clearing", this.#t);
    else if ((data as { ok?: boolean } | undefined)?.ok === false) this._error = this.#t("bluebillywig_cacheClearFailed");
    else this._message = this.#t("bluebillywig_cacheCleared");
    this._busy = false;
  }

  /**
   * The uninstall equivalent. Umbraco has no uninstall hook for a package, so this explicit action empties what the package
   * stored (settings, secrets, per-clip playouts) by a fixed list of keys, and nothing else.
   */
  async #cleanup() {
    if (
      !(await this.#confirm(
        this.#t("bluebillywig_cleanupHeadline"),
        this.#t("bluebillywig_cleanupContent"),
        this.#t("bluebillywig_cleanupConfirm"),
        "danger",
      ))
    )
      return;
    this._busy = true;
    this._message = this._error = undefined;
    const { data, error } = await tryExecute(this, postSettingsCleanup({ body: { confirm: true } }), { disableNotifications: true });
    if (error || !data) {
      this._error = saveError(error, "cleanup", this.#t);
    } else {
      this._message = this.#t(data.cleared.length === 1 ? "bluebillywig_removedOne" : "bluebillywig_removedMany", data.cleared.length);
      const reloaded = await tryExecute(this, getSettings());
      if (reloaded.data) this._settings = reloaded.data;
      void this.#loadPlayouts();
    }
    this._busy = false;
  }

  /**
   * Tests the values as typed in the form, before they are saved (the plugin's test_connection). A secret left empty means the
   * stored one, so the test works without retyping it. Nothing is stored by the test.
   */
  async #test() {
    if (!this._settings) return;
    this._busy = true;
    this._message = this._error = undefined;
    const { publication, apiId, apiSecret } = this._settings;
    const { data, error } = await tryExecute(this, postSettingsTestConnection({ body: { publication, apiId, apiSecret } }), { disableNotifications: true });
    if (error || !data) this._error = errorText(error) ?? this.#t("bluebillywig_testFailedToRun");
    else if (data.ok) this._message = this.#t("bluebillywig_connectedTo", data.publication ?? "Blue Billywig");
    else this._error = data.error ?? this.#t("bluebillywig_connectionFailed");
    this._busy = false;
  }

  /**
   * The plugin's disconnect: removes the stored publication, API keys and secrets and clears the caches. Values set in
   * configuration cannot be removed here, and the notice says so instead of claiming a disconnect that did not happen.
   */
  async #disconnect() {
    if (
      !(await this.#confirm(
        this.#t("bluebillywig_disconnectHeadline"),
        this.#t("bluebillywig_disconnectContent"),
        this.#t("bluebillywig_disconnect"),
        "danger",
      ))
    )
      return;
    this._busy = true;
    this._message = this._error = undefined;
    const { data, error } = await tryExecute(this, postSettingsDisconnect({ body: { confirm: true } }), { disableNotifications: true });
    if (error || !data) {
      this._error = errorText(error) ?? this.#t("bluebillywig_disconnectFailed");
    } else {
      this._settings = data.settings;
      if (data.stillConfigured.length > 0) {
        this._error = this.#t(data.stillConfigured.length === 1 ? "bluebillywig_stillConfiguredOne" : "bluebillywig_stillConfiguredMany", data.stillConfigured.join(", "));
      } else if (!data.cacheCleared) {
        this._error = this.#t("bluebillywig_disconnectedCacheFailed");
      } else {
        this._message = this.#t("bluebillywig_disconnected");
      }
      void this.#loadPlayouts();
    }
    this._busy = false;
  }

  #playoutOptions(current: string) {
    const known = this._playouts.map((p) => ({ name: p.label, value: p.value, selected: p.value === current }));
    // A saved playout that the list does not hold (or an unreachable list) stays selectable, so saving never changes it silently.
    if (!known.some((o) => o.value === current)) known.unshift({ name: current, value: current, selected: true });
    return known;
  }

  override render() {
    const s = this._settings;
    if (!s) {
      return this._error ? html`<uui-box><p class="error">${this._error}</p></uui-box>` : html`<uui-loader></uui-loader>`;
    }
    return html`<uui-box headline=${this.#t("bluebillywig_connectionHeadline")}>
        ${this.#text(this.#t("bluebillywig_publication"), "publication", "publication", { placeholder: "my-publication" })}
        ${this.#baseUrl()}
        ${this.#text(this.#t("bluebillywig_apiId"), "apiId", "apiId")}
        ${this.#text(this.#t("bluebillywig_apiSecret"), "apiSecret", "apiSecret", { type: "password", placeholder: s.hasApiSecret ? this.#t("bluebillywig_secretSaved") : "" })}
        <p class="hint">${this.#t("bluebillywig_readOnlyHint")}</p>
        ${this.#text(this.#t("bluebillywig_readOnlyApiId"), "readOnlyApiId", "roApiId")}
        ${this.#text(this.#t("bluebillywig_readOnlyApiSecret"), "readOnlyApiSecret", "roApiSecret", { type: "password", placeholder: s.hasReadOnlyApiSecret ? this.#t("bluebillywig_secretSaved") : "" })}
      </uui-box>
      <uui-box headline=${this.#t("bluebillywig_defaultsHeadline")}>
        <umb-property-layout label=${this.#t("bluebillywig_defaultPlayout")} description=${this._playoutError ?? this.#t("bluebillywig_defaultPlayoutHelp")}>
          <uui-select
            slot="editor"
            id="bb-settings-playout"
            label=${this.#t("bluebillywig_defaultPlayout")}
            .options=${this.#playoutOptions(s.playout)}
            @change=${(e: Event) => this.#set("playout", (e.target as HTMLSelectElement).value)}></uui-select>
        </umb-property-layout>
        <umb-property-layout label=${this.#t("bluebillywig_defaultEmbedType")}>
          <uui-select
            slot="editor"
            label=${this.#t("bluebillywig_defaultEmbedType")}
            .options=${EMBED_TYPES.map((v) => ({ name: v, value: v, selected: v === s.embed }))}
            @change=${(e: Event) => this.#set("embed", (e.target as HTMLSelectElement).value)}></uui-select>
        </umb-property-layout>
        ${this.#text(this.#t("bluebillywig_videosPerPage"), "pageCount", undefined, { type: "number" })}
        <umb-property-layout label=${this.#t("bluebillywig_customFields")}>
          <uui-toggle slot="editor" label=${this.#t("bluebillywig_customFields")} .checked=${s.enableCustomFields} @change=${(e: Event) => this.#set("enableCustomFields", (e.target as HTMLInputElement).checked)}></uui-toggle>
        </umb-property-layout>
        <umb-property-layout
          label=${this.#t("bluebillywig_consentMessage")}
          description=${this.#t(this.#pinned("consentMessage") ? "bluebillywig_consentMessagePinned" : "bluebillywig_consentMessageHelp")}>
          <uui-textarea
            slot="editor"
            id="bb-settings-consent-message"
            label=${this.#t("bluebillywig_consentMessage")}
            maxlength="500"
            ?readonly=${this.#pinned("consentMessage")}
            placeholder="Watching this video will transmit data to Blue Billywig, our video provider. Clicking Load will grant consent for all Blue Billywig videos on this page."
            .value=${s.consentMessage ?? ""}
            @input=${(e: Event) => this.#set("consentMessage", (e.target as HTMLTextAreaElement).value)}></uui-textarea>
        </umb-property-layout>
        <umb-property-layout label=${this.#t("bluebillywig_ctaEditor")}>
          <uui-toggle slot="editor" label=${this.#t("bluebillywig_ctaEditor")} .checked=${s.enableCta} @change=${(e: Event) => this.#set("enableCta", (e.target as HTMLInputElement).checked)}></uui-toggle>
        </umb-property-layout>
      </uui-box>
      <div class="actions">
        <uui-button look="primary" label=${this.#t("bluebillywig_save")} ?disabled=${this._busy} @click=${this.#save}></uui-button>
        <uui-button id="bb-settings-test" look="secondary" label=${this.#t("bluebillywig_testConnection")} ?disabled=${this._busy} @click=${this.#test}></uui-button>
        <uui-button id="bb-settings-disconnect" look="secondary" color="danger" label=${this.#t("bluebillywig_disconnect")} ?disabled=${this._busy} @click=${() => void this.#disconnect()}></uui-button>
        <uui-button id="bb-settings-clear-cache" look="secondary" label=${this.#t("bluebillywig_clearCache")} ?disabled=${this._busy} @click=${() => void this.#clearCache()}></uui-button>
        ${this._message ? html`<span id="bb-settings-message">${this._message}</span>` : nothing}
        ${this._error ? html`<span id="bb-settings-error" class="error">${this._error}</span>` : nothing}
      </div>
      ${this.#gateBox()}
      <uui-box headline=${this.#t("bluebillywig_cleanupBox")}>
        <p class="hint">${this.#t("bluebillywig_cleanupHint")}</p>
        <uui-button id="bb-settings-cleanup" look="primary" color="danger" label=${this.#t("bluebillywig_cleanupBox")} ?disabled=${this._busy} @click=${() => void this.#cleanup()}></uui-button>
      </uui-box>`;
  }

  static override styles = css`
    :host { display: block; padding: var(--uui-size-layout-1); }
    uui-box { margin-bottom: var(--uui-size-space-5); }
    .actions { display: flex; align-items: center; gap: var(--uui-size-space-3); }
    .error { color: var(--uui-color-danger); }
    .hint { margin: 0 0 var(--uui-size-space-4); opacity: 0.8; font-size: 12px; }
  `;
}

export default BbSettingsDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-settings-dashboard": BbSettingsDashboardElement;
  }
}
