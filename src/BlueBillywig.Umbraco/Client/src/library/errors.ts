import { englishTerm, type Term } from "../localization/terms.js";

/** The HTTP status of a failed API call, or 0 when it never got an answer. */
export function statusOf(error: unknown): number {
  const status = (error as { status?: number } | undefined)?.status;
  return typeof status === "number" ? status : 0;
}

/**
 * The text for a failed library call. A SAPI failure is always said out loud, so it can never be mistaken for an
 * empty library: the server answers 502 for it and this view shows an error, not "no videos".
 */
export function libraryError(error: unknown, what: string, t: Term = englishTerm): string {
  const noun = nounOf(what, t);
  switch (statusOf(error)) {
    case 502:
      return t("bluebillywig_loadFailedGateway", noun);
    case 401:
    case 403:
      return t("bluebillywig_noAccess", noun);
    case 400:
      return t("bluebillywig_badRequest", noun);
    default:
      return t("bluebillywig_loadFailed", noun);
  }
}

/**
 * The translated noun for what failed ("videos", "embed code"): the key is `bluebillywig_noun` plus the words capitalised. A
 * noun without a key (a caller not moved to translations yet) is used as written, so nothing ever shows a raw key.
 */
export function nounOf(what: string, t: Term = englishTerm): string {
  const key = "bluebillywig_noun" + what.split(/\s+/).map((w) => w.charAt(0).toUpperCase() + w.slice(1)).join("");
  const noun = t(key);
  return noun === key ? what : noun;
}

export function formatDuration(seconds?: number | null): string {
  if (!seconds || seconds < 0) return "";
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

export function formatDate(iso?: string | null): string {
  if (!iso) return "";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

/**
 * The message of a failed edit call. The server's own refusals (400, 404, 409, 422) carry a short sentence that is safe
 * to show ("Invalid status value", "These custom fields are no longer defined..."). A 502 never carries upstream text:
 * it is always the generic sentence below, so a Blue Billywig outage cannot be mistaken for an accepted change.
 */
export function saveError(error: unknown, what = "change", t: Term = englishTerm): string {
  const status = statusOf(error);
  const noun = nounOf(what, t);
  if (status === 502) return t("bluebillywig_saveGateway", noun);
  if (status === 401 || status === 403) return t("bluebillywig_saveNoPermission", noun);
  if (status === 404) return t("bluebillywig_clipGone");
  // Our own refusals (400, 409, 422) are problem details whose detail is the sentence for the editor.
  const detail = (error as { problemDetails?: { detail?: unknown } } | undefined)?.problemDetails?.detail;
  if ([400, 409, 422].includes(status) && typeof detail === "string" && detail.length > 0) return detail;
  return t("bluebillywig_saveFailed", noun);
}

/**
 * The sentence for an editor out of a failed call from a settings-style form. The server's refusals are problem details
 * whose `error` extension and `detail` carry the sentence (see the RefusalShape filter on the server), so those come first;
 * a validation problem's first message is next. The title is the HTTP status text ("Bad Request") and only a last resort.
 * Undefined when there is nothing to say, so the caller can use its own fallback sentence.
 */
export function errorText(error: unknown): string | undefined {
  if (!error) return undefined;
  const e = error as {
    message?: unknown;
    detail?: unknown;
    title?: unknown;
    error?: unknown;
    body?: { error?: unknown };
    problemDetails?: { error?: unknown; detail?: unknown; errors?: unknown; title?: unknown };
  };
  const text = (v: unknown): string | undefined => (typeof v === "string" && v.trim().length > 0 ? v : undefined);
  const p = e.problemDetails;
  return (
    text(p?.error) ??
    text(p?.detail) ??
    firstOf(p?.errors) ??
    text(e.body?.error) ??
    text(e.error) ??
    text(e.detail) ??
    text(e.message) ??
    text(p?.title) ??
    text(e.title)
  );
}

/** The first message of a problem details `errors` member: a list of sentences, or a map from field to a list of them. */
function firstOf(errors: unknown): string | undefined {
  if (Array.isArray(errors)) return errors.find((m): m is string => typeof m === "string" && m.trim().length > 0);
  if (errors && typeof errors === "object") {
    for (const list of Object.values(errors)) {
      const first = firstOf(list);
      if (first) return first;
    }
  }
  return undefined;
}

/**
 * The address the API calls default to for a publication, shown as the placeholder of the read-only base URL field. Empty
 * until the publication is valid (a lowercase DNS label with dots and dashes, 3 to 63 characters), so a half-typed or
 * refused value never previews an address that would not resolve.
 */
export function defaultApiAddress(publication?: string | null): string {
  const p = publication ?? "";
  const valid = p.length >= 3 && p.length <= 63 && /^[a-z0-9]([a-z0-9-]|\.(?!\.))*[a-z0-9]$/.test(p) && !p.includes("..");
  return valid ? `https://${p}.bbvms.com` : "";
}
