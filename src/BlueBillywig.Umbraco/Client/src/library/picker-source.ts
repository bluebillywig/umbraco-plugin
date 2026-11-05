/**
 * The picker variant's live stream toggle and recent videos list (the reference plugin's block picker has both).
 * Pure helpers, so the rules are testable without a DOM.
 */

/** The source type the picker asks for: the live toggle narrows the list to live streams, otherwise every source type. */
export function pickerSourceType(liveOnly: boolean): string | undefined {
  return liveOnly ? "live" : undefined;
}

/**
 * Whether the toggle is offered: only when a live source-type query found at least one. A failed call hides the toggle
 * (the reference plugin does the same), because the toggle is an extra, not a way to reach a clip.
 */
export function hasLiveStreams(data: { total?: number } | undefined, error: unknown): boolean {
  return !error && (data?.total ?? 0) > 0;
}

/** Picking a folder leaves the live view, like the reference plugin's sidebar where both are one selection. */
export function selectFolder(folder: string): { folder: string; liveOnly: false } {
  return { folder, liveOnly: false };
}

/** Turning the live toggle on clears the folder; turning it off returns to the whole library. */
export function selectLive(on: boolean): { folder: string; liveOnly: boolean } {
  return { folder: "", liveOnly: on };
}

/**
 * The list is "recent videos" while nothing narrows it: the library is listed newest first, so with no search, folder,
 * live toggle, media type or dates the first page is the recent videos.
 */
export function isRecentList(f: { search: string; folder: string; liveOnly: boolean; mediaType: string; from: string; to: string }): boolean {
  return f.search === "" && f.folder === "" && !f.liveOnly && (f.mediaType === "all" || f.mediaType === "") && f.from === "" && f.to === "";
}

/**
 * The list heading: live streams while the toggle is on, unless a search is typed (then the list is the search's result);
 * recent videos while nothing narrows it; search results otherwise.
 */
export function listHeadingKey(f: { search: string; folder: string; liveOnly: boolean; mediaType: string; from: string; to: string }): "bluebillywig_liveStreams" | "bluebillywig_recentVideos" | "bluebillywig_searchResults" {
  if (f.liveOnly) return f.search.trim() === "" ? "bluebillywig_liveStreams" : "bluebillywig_searchResults";
  return isRecentList(f) ? "bluebillywig_recentVideos" : "bluebillywig_searchResults";
}
