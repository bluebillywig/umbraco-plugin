import { css, customElement, html, LitElement, nothing, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getAnalyticsDashboard, getStatus, type DashboardAnalyticsResponseModel } from "../api/index.js";
import { statusOf } from "../library/errors.js";
import { SETTINGS_PATH } from "../shared/credentials-banner.element.js";
import "../shared/credentials-banner.element.js";

const DEVICE_COLORS = ["#2271b1", "#d63638", "#dba617", "#00a32a", "#8c8f94"];

const number = (n: number) => n.toLocaleString();

/**
 * The analytics widget of the Content dashboard: views and player loads of the last seven days, the play rate, the five
 * most viewed videos and the device split. Port of the plugin's dashboard widget (render_dashboard_widget).
 *
 * A failed SAPI call is a red panel naming the part that failed, never zeros: "no views" and "could not load" must not
 * look alike. The server does not cache a failure, so reloading shows the numbers as soon as SAPI answers again.
 */
@customElement("bb-analytics-dashboard")
export class BbAnalyticsDashboardElement extends UmbElementMixin(LitElement) {
  @state() private _configured?: boolean;
  @state() private _data?: DashboardAnalyticsResponseModel;
  @state() private _error?: string;
  @state() private _loading = true;

  override async connectedCallback() {
    super.connectedCallback();
    const status = await tryExecute(this, getStatus());
    this._configured = status.data?.configured;
    if (this._configured === false) {
      this._loading = false;
      return;
    }
    await this.#load();
  }

  async #load() {
    this._loading = true;
    this._error = undefined;
    const { data, error } = await tryExecute(this, getAnalyticsDashboard());
    this._loading = false;
    if (error || !data) {
      const sections = (error as { problemDetails?: { sections?: string[] } } | undefined)?.problemDetails?.sections;
      this._data = undefined;
      this._error =
        statusOf(error) === 502 && sections?.length
          ? `Unable to load analytics (${sections.join(", ")}). See the Umbraco log for details.`
          : "Unable to load analytics. See the Umbraco log for details.";
      return;
    }
    this._data = data;
  }

  #stats(d: DashboardAnalyticsResponseModel) {
    return html`<div class="stats">
      <div class="stat"><div class="value" id="bb-stat-views">${number(d.totalViews)}</div><div class="label">Views (7d)</div></div>
      <div class="stat"><div class="value" id="bb-stat-inits">${number(d.totalInits)}</div><div class="label">Player loads</div></div>
      <div class="stat"><div class="value" id="bb-stat-playrate">${d.playRate}%</div><div class="label">Play rate</div></div>
    </div>`;
  }

  #body() {
    if (this._configured === false) {
      return html`<p id="bb-analytics-unconfigured">Configure your <a href=${SETTINGS_PATH}>Blue Billywig API credentials</a> to see analytics.</p>`;
    }
    if (this._loading) return html`<uui-loader-bar></uui-loader-bar>`;
    if (this._error) {
      return html`<p class="error" id="bb-analytics-error" role="alert">${this._error}</p>
        <uui-button label="Try again" look="secondary" compact @click=${() => void this.#load()}>Try again</uui-button>`;
    }
    const d = this._data;
    if (!d) return nothing;
    if (d.totalViews === 0 && d.totalInits === 0) {
      return html`<p class="empty" id="bb-analytics-empty">No video views recorded in the last 7 days. Embed videos to start tracking analytics.</p>`;
    }
    return html`${this.#stats(d)}
      ${d.topVideos.length > 0
        ? html`<div class="section">
            <div class="title">Top videos</div>
            <table id="bb-analytics-top">
              ${d.topVideos.slice(0, 5).map((v) => html`<tr><td>${v.value}</td><td>${number(v.count)}</td></tr>`)}
            </table>
          </div>`
        : nothing}
      ${d.devices.length > 0
        ? html`<div class="section">
            <div class="title">Devices</div>
            <div class="bar" role="img" aria-label="Device split">
              ${d.devices.map((x, i) => html`<span style="width:${x.percent}%;background:${DEVICE_COLORS[i % 5]}"></span>`)}
            </div>
            <div class="legend" id="bb-analytics-devices">
              ${d.devices.map((x, i) => html`<span><i style="background:${DEVICE_COLORS[i % 5]}"></i>${x.name} ${x.percent}%</span>`)}
            </div>
          </div>`
        : nothing}
      <p class="period"><small>${d.from} — ${d.to}</small></p>`;
  }

  override render() {
    return html`<bb-credentials-banner></bb-credentials-banner><uui-box headline="Blue Billywig video analytics">${this.#body()}</uui-box>`;
  }

  static override styles = css`
    :host { display: block; padding: var(--uui-size-layout-1); }
    .stats { display: flex; gap: var(--uui-size-space-5); margin-bottom: var(--uui-size-space-5); }
    .stat { flex: 1; text-align: center; padding: var(--uui-size-space-4); background: var(--uui-color-surface-alt, #f6f7f7); border-radius: var(--uui-border-radius); }
    .value { font-size: 24px; font-weight: 700; }
    .label { font-size: 11px; text-transform: uppercase; opacity: 0.7; margin-top: 2px; }
    .section { margin-top: var(--uui-size-space-5); }
    .title { font-size: 11px; text-transform: uppercase; opacity: 0.7; margin-bottom: 6px; }
    table { width: 100%; border-collapse: collapse; }
    td { padding: 6px 0; border-bottom: 1px solid var(--uui-color-divider, #f0f0f0); font-size: 13px; }
    td:last-child { text-align: right; font-weight: 600; }
    .bar { display: flex; height: 6px; border-radius: 3px; overflow: hidden; }
    .bar span { display: block; }
    .legend { display: flex; flex-wrap: wrap; gap: 8px 12px; margin-top: 6px; font-size: 11px; }
    .legend i { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 4px; }
    .error { color: var(--uui-color-danger); text-align: center; padding: 20px 0 8px; }
    .empty { text-align: center; opacity: 0.7; padding: 20px 0; }
    .period { text-align: right; margin: 10px 0 0; }
  `;
}

export default BbAnalyticsDashboardElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-analytics-dashboard": BbAnalyticsDashboardElement;
  }
}
