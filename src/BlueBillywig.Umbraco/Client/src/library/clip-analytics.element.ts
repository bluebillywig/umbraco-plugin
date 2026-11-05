import { css, customElement, html, LitElement, nothing, property, state } from "@umbraco-cms/backoffice/external/lit";
import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getAnalyticsClipsById, type ClipAnalyticsResponseModel } from "../api/index.js";
import { statusOf } from "./errors.js";

const RANGES = [
  { days: 7, label: "7d" },
  { days: 30, label: "30d" },
  { days: 90, label: "90d" },
];

const W = 400;
const H = 160;

/**
 * The retention curve as the plugin draws it (bb-clip-analytics.js): the share of viewers still watching at each percent
 * of the clip, in a 400x160 box with 10 units of headroom, closed at the bottom. Exported for the check in the browser.
 */
export function retentionPath(retention: number[], views: number): string {
  const points = retention.map((count, x) => `${((x / 100) * W).toFixed(1)},${(H - (count / views) * 150).toFixed(1)}`);
  return `M0,${H} L${points.join(" L")} L${W},${H} Z`;
}

/**
 * Per-clip engagement in the clip editor: views, play rate, average view time, the retention curve (inline SVG, with the
 * same numbers in a table for screen readers), top countries and devices. Port of the plugin's analytics panel.
 */
@customElement("bb-clip-analytics")
export class BbClipAnalyticsElement extends UmbElementMixin(LitElement) {
  @property({ type: Number }) clipId = 0;
  @state() private _days = 30;
  @state() private _data?: ClipAnalyticsResponseModel;
  @state() private _error?: string;
  @state() private _loading = false;

  #seq = 0;

  override connectedCallback() {
    super.connectedCallback();
    void this.#load();
  }

  async #load() {
    if (!this.clipId) return;
    const seq = ++this.#seq;
    this._loading = true;
    this._error = undefined;
    const { data, error } = await tryExecute(this, getAnalyticsClipsById({ path: { id: this.clipId }, query: { days: this._days } }));
    if (seq !== this.#seq) return;
    this._loading = false;
    if (error || !data) {
      this._data = undefined;
      const sections = (error as { problemDetails?: { sections?: string[] } } | undefined)?.problemDetails?.sections;
      this._error = statusOf(error) === 502 && sections ? "Could not load analytics from Blue Billywig." : "Could not load analytics.";
      return;
    }
    this._data = data;
  }

  #setDays(days: number) {
    if (days === this._days) return;
    this._days = days;
    void this.#load();
  }

  #breakdown(title: string, id: string, rows: Array<{ value: string; count: number }>, views: number) {
    return html`<div class="col">
      <h4>${title}</h4>
      <ul id=${id}>
        ${rows.length === 0
          ? html`<li class="hint">No data.</li>`
          : rows.map((r) => html`<li><span>${r.value}</span><span>${r.count} (${views > 0 ? Math.round((r.count / views) * 100) : 0}%)</span></li>`)}
      </ul>
    </div>`;
  }

  #curve(d: ClipAnalyticsResponseModel) {
    const deciles = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
    return html`<div class="chart-wrap">
      <div class="chart-title" id="bb-retention-title">Retention curve</div>
      <svg id="bb-retention" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-labelledby="bb-retention-title">
        <desc>Percentage of viewers still watching at each point in the clip.</desc>
        <path id="bb-retention-curve" d=${retentionPath(d.retention, d.views)} fill="rgba(30,74,212,0.15)" stroke="#1e4ad4" stroke-width="1.5"></path>
      </svg>
      <div class="axis"><span>0%</span><span>50%</span><span>100% of clip</span></div>
      <details>
        <summary>Retention data (table)</summary>
        <table id="bb-retention-table">
          <thead><tr><th scope="col">% of clip</th><th scope="col">Viewers still watching</th></tr></thead>
          <tbody>${deciles.map((x) => html`<tr><td>${x}%</td><td>${d.retention[x] ?? 0} (${d.views > 0 ? Math.round(((d.retention[x] ?? 0) / d.views) * 100) : 0}%)</td></tr>`)}</tbody>
        </table>
      </details>
    </div>`;
  }

  #body() {
    if (this._error) return html`<p class="error" id="bb-clip-analytics-error" role="alert">${this._error}</p>`;
    const d = this._data;
    if (this._loading || !d) return html`<uui-loader-bar></uui-loader-bar>`;
    if (d.views === 0) return html`<p class="hint" id="bb-clip-analytics-empty">No views in this window.</p>`;
    return html`<div class="cards">
        <div class="card"><span class="metric" id="bb-ca-views">${d.views.toLocaleString()}</span><span class="label">Views</span></div>
        <div class="card"><span class="metric" id="bb-ca-playrate">${d.playRate === null ? "—" : `${(d.playRate * 100).toFixed(1)}%`}</span><span class="label">Play rate</span></div>
        <div class="card"><span class="metric" id="bb-ca-avg">${d.avgViewTime === null ? "—" : `${d.avgViewTime.toFixed(1)}s`}</span><span class="label">Avg view time</span></div>
      </div>
      ${d.initsFailed ? html`<p class="hint" id="bb-ca-inits-failed">The play rate could not be loaded.</p>` : nothing}
      ${this.#curve(d)}
      <div class="breakdown">${this.#breakdown("Top countries", "bb-ca-countries", d.countries, d.views)}${this.#breakdown("Devices", "bb-ca-devices", d.devices, d.views)}</div>`;
  }

  override render() {
    return html`<uui-box headline="Engagement">
      <div class="ranges" role="group" aria-label="Date range">
        ${RANGES.map((r) => html`<uui-button class=${r.days === this._days ? "range active" : "range"} data-days=${r.days} look=${r.days === this._days ? "primary" : "secondary"} compact label=${r.label} @click=${() => this.#setDays(r.days)}>${r.label}</uui-button>`)}
      </div>
      ${this.#body()}
    </uui-box>`;
  }

  static override styles = css`
    :host { display: block; }
    .ranges { display: flex; gap: var(--uui-size-space-2); margin-bottom: var(--uui-size-space-4); }
    .cards { display: flex; gap: var(--uui-size-space-4); }
    .card { flex: 1; text-align: center; padding: var(--uui-size-space-3); background: var(--uui-color-surface-alt, #f6f7f7); border-radius: var(--uui-border-radius); }
    .metric { display: block; font-size: 20px; font-weight: 700; }
    .label { font-size: 11px; text-transform: uppercase; opacity: 0.7; }
    .chart-wrap { margin-top: var(--uui-size-space-5); }
    .chart-title { font-weight: 600; margin-bottom: 4px; }
    svg { width: 100%; height: 160px; display: block; background: var(--uui-color-surface-alt, #f6f7f7); }
    .axis { display: flex; justify-content: space-between; font-size: 11px; opacity: 0.7; }
    table { border-collapse: collapse; margin-top: 6px; }
    th, td { text-align: left; padding: 2px 12px 2px 0; font-size: 12px; }
    .breakdown { display: flex; gap: var(--uui-size-space-5); margin-top: var(--uui-size-space-5); }
    .col { flex: 1; }
    h4 { margin: 0 0 4px; font-size: 13px; }
    ul { list-style: none; margin: 0; padding: 0; font-size: 13px; }
    li { display: flex; justify-content: space-between; padding: 2px 0; }
    .hint { opacity: 0.7; font-size: 12px; }
    .error { color: var(--uui-color-danger); }
  `;
}

export default BbClipAnalyticsElement;

declare global {
  interface HTMLElementTagNameMap {
    "bb-clip-analytics": BbClipAnalyticsElement;
  }
}
