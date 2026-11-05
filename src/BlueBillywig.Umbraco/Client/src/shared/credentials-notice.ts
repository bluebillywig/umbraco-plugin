/**
 * The rules of the backoffice-wide "not configured yet" notice, kept apart from the Lit element so they can be tested in
 * Node. The reference plugin shows its admin notice on every admin page, to administrators only (the manifest's
 * Umb.Condition.CurrentUser.IsAdmin), hides it on its own settings page and lets it be dismissed for the current page load only: the dismissal is
 * held in the element's memory, not in localStorage or sessionStorage, so a reload brings the notice back while the package is unconfigured.
 */

/** The settings dashboard, where the credentials are entered (a Settings section dashboard with pathname "blue-billywig"). */
export const SETTINGS_PATH = "/umbraco/section/settings/dashboard/blue-billywig";

/** Whether the location is the package's settings dashboard (trailing slashes ignored). */
export function isSettingsPath(pathname: string): boolean {
  return pathname.replace(/\/+$/, "").endsWith(SETTINGS_PATH);
}

/** Shown only when the status says "not configured" (unknown status shows nothing), off the settings dashboard, and not dismissed. */
export function shouldShowNotice(state: { configured?: boolean; onSettings: boolean; dismissed: boolean }): boolean {
  return state.configured === false && !state.onSettings && !state.dismissed;
}
