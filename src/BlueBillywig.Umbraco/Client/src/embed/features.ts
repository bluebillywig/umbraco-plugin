import type { VideoType } from "./video-value.js";

export interface EmbedFeatures {
  hasChannels: boolean;
  hasShorts: boolean;
}

/**
 * The source types the picker offers (the reference plugin registers its channel and shorts blocks only when the publication has
 * any). Clips are always offered. Without an answer (the call failed, or has not come back) every type is offered, so a
 * failing count never hides a type. A type the stored value already uses stays offered, so an existing channel or shorts
 * embed can still be seen and changed after the last one was removed upstream.
 */
export function offeredTypes(all: readonly VideoType[], features: EmbedFeatures | undefined, current?: VideoType): VideoType[] {
  if (!features) return [...all];
  return all.filter((t) => t === "clip" || t === current || (t === "channel" ? features.hasChannels : features.hasShorts));
}

/** The features from a call's result, or undefined when the call failed or answered something unexpected. */
export function readFeatures(data: unknown, error: unknown): EmbedFeatures | undefined {
  if (error || !data || typeof data !== "object") return undefined;
  const { hasChannels, hasShorts } = data as Partial<EmbedFeatures>;
  return typeof hasChannels === "boolean" && typeof hasShorts === "boolean" ? { hasChannels, hasShorts } : undefined;
}
