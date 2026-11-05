import type { ClipUpdateRequestModel } from "../api/index.js";

/** What the editor needs to know about a clip to say it will not play: the picker's clip summary has these fields. */
export interface StatusFields {
  status?: string | null;
  embargoFrom?: string | null;
  embargoTo?: string | null;
}

export type BannerKind = "draft" | "archived" | "embargo";
export type BannerAction = "publish" | "removeEmbargo";

export interface ClipBanner {
  /** Decides the sentence and the colour. A draft or archived clip wins over an embargo, like the reference plugin's banner. */
  kind: BannerKind;
  /** The active embargo window, shown when it decides the sentence. */
  embargo?: { from: string; to: string };
  actions: BannerAction[];
}

function time(value?: string | null): number {
  return value ? Date.parse(value) : Number.NaN;
}

/**
 * The status banner of the embed editor: shown for a draft, an archived or an embargoed clip, otherwise nothing. The embargo
 * counts only while now lies inside its window and both ends are readable dates. The reference plugin compares the ISO
 * strings; the dates are parsed here, because strings in different zones do not compare as times.
 */
export function clipBanner(clip: StatusFields | undefined, now: number = Date.now()): ClipBanner | undefined {
  if (!clip) return undefined;
  const from = time(clip.embargoFrom);
  const to = time(clip.embargoTo);
  const embargoed = !Number.isNaN(from) && !Number.isNaN(to) && now >= from && now <= to;
  const draft = clip.status === "draft";
  const archived = clip.status === "archived";
  if (!draft && !archived && !embargoed) return undefined;

  const actions: BannerAction[] = [];
  if (draft || archived) actions.push("publish");
  if (embargoed) actions.push("removeEmbargo");
  return {
    kind: draft ? "draft" : archived ? "archived" : "embargo",
    embargo: embargoed ? { from: clip.embargoFrom as string, to: clip.embargoTo as string } : undefined,
    actions,
  };
}

/** The request body of an action: the ordinary clip update, with only the fields the action changes. */
export function actionRequest(action: BannerAction): ClipUpdateRequestModel {
  return action === "publish" ? { status: "published" } : { embargoFrom: "", embargoTo: "" };
}

/**
 * Whether the saved clip the server returned shows the change. Success is claimed only when it does: the server answers
 * with the clip read back from Blue Billywig, so an embargo that the platform kept (bb.dev ignores an empty date) is never
 * reported as removed. A date counts as gone when it is empty or no longer a readable date.
 */
export function actionTook(action: BannerAction, saved: StatusFields | undefined): boolean {
  if (!saved) return false;
  return action === "publish" ? saved.status === "published" : Number.isNaN(time(saved.embargoFrom)) || Number.isNaN(time(saved.embargoTo));
}

/** The held clip patched with what the server reports back (only the fields the action changes). */
export function applySaved<T extends StatusFields>(clip: T, action: BannerAction, saved: StatusFields): T {
  return action === "publish" ? { ...clip, status: saved.status } : { ...clip, embargoFrom: saved.embargoFrom ?? null, embargoTo: saved.embargoTo ?? null };
}

/** An ISO date as shown in the banner, in UTC: `2030-01-01 10:00`. The banner text names the zone. */
export function embargoText(iso: string): string {
  const t = Date.parse(iso);
  return (Number.isNaN(t) ? iso : new Date(t).toISOString()).slice(0, 16).replace("T", " ");
}
