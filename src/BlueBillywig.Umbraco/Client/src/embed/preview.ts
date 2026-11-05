import type { VideoValue } from "./video-value.js";

// The frame attributes of the details modal's preview, shared here so both previews are the same: the player page runs on
// the player host (never inline in the backoffice), without a referrer, and can start playback and go fullscreen.
export const PREVIEW_SANDBOX = "allow-scripts allow-same-origin allow-presentation allow-popups";
export const PREVIEW_ALLOW = "autoplay; fullscreen";
export const PREVIEW_REFERRER_POLICY = "no-referrer";

const HEIGHT = /^\d{1,4}(\.\d{1,2})?(px|vh|em|rem|%)$/;

export interface PreviewQuery {
  id: number;
  type: VideoValue["type"];
  playout?: string;
  publication?: string;
}

/** The query of the preview call. Only what the player URL depends on is sent. */
export function previewQuery(value: VideoValue): PreviewQuery {
  return {
    id: value.id,
    type: value.type,
    ...(value.type === "clip" && value.playout ? { playout: value.playout } : {}),
    ...(value.publication ? { publication: value.publication } : {}),
  };
}

/**
 * Identifies the preview a value would show. A loaded preview belongs to one key: when the type, id, playout or
 * publication changes, the loaded frame is dropped and the "Load preview" button comes back (the token was minted for the
 * old URL, and nothing loads from the player host until the editor asks).
 */
export function previewKey(value: VideoValue | undefined): string {
  if (!value) return "";
  const q = previewQuery(value);
  return [q.type, q.id, q.playout ?? "", q.publication ?? ""].join("|");
}

/** The CSS height of the frame for a channel or shorts (their configured height, when valid); clips use a 16:9 box. */
export function previewHeight(value: VideoValue): string | undefined {
  if (value.type === "clip") return undefined;
  const fallback = value.type === "channel" ? "600px" : "700px";
  return value.height && HEIGHT.test(value.height) ? value.height : fallback;
}
