// The value the property editor stores. Matches BlueBillywigVideo on the server and the reference plugin's block attributes.
export type VideoType = "clip" | "channel" | "shorts";

export interface VideoValue {
  type: VideoType;
  id: number;
  /** Another publication than the configured one; kept as stored, never chosen in the editor. */
  publication?: string;
  playout?: string;
  showTranscript?: boolean;
  showChapters?: boolean;
  lazyLoad?: boolean;
  height?: string;
  fullscreen?: boolean;
}

export const DEFAULT_CHANNEL_HEIGHT = "600px";
export const DEFAULT_SHORTS_HEIGHT = "700px";

/** Starting values for a type, the same defaults the reference plugin's blocks have. */
export function defaultsFor(type: VideoType, id: number): VideoValue {
  switch (type) {
    case "clip":
      return { type, id, showTranscript: true, showChapters: false, lazyLoad: true };
    case "channel":
      return { type, id, height: DEFAULT_CHANNEL_HEIGHT, lazyLoad: false };
    case "shorts":
      return { type, id, height: DEFAULT_SHORTS_HEIGHT, fullscreen: false };
  }
}

/** The editor can be handed an object, a JSON string or nothing. Anything unusable is "no value". */
export function readValue(raw: unknown): VideoValue | undefined {
  let value = raw;
  if (typeof raw === "string") {
    try {
      value = JSON.parse(raw);
    } catch {
      return undefined;
    }
  }
  if (!value || typeof value !== "object") return undefined;
  const v = value as Partial<VideoValue>;
  const id = Number(v.id);
  if (!Number.isInteger(id) || id <= 0) return undefined;
  if (v.type !== "clip" && v.type !== "channel" && v.type !== "shorts") return undefined;
  return { ...defaultsFor(v.type, id), ...v, id };
}
