import { describe, expect, it } from "vitest";
import { DEFAULT_CHANNEL_HEIGHT, DEFAULT_SHORTS_HEIGHT, defaultsFor, readValue, selectionChanged } from "../src/embed/video-value";

describe("defaultsFor", () => {
  it("gives a clip the transcript, no chapters and lazy loading", () => {
    expect(defaultsFor("clip", 7)).toEqual({ type: "clip", id: 7, showTranscript: true, showChapters: false, lazyLoad: true });
  });

  it("gives a channel its height and eager loading", () => {
    expect(defaultsFor("channel", 7)).toEqual({ type: "channel", id: 7, height: "600px", lazyLoad: false });
    expect(DEFAULT_CHANNEL_HEIGHT).toBe("600px");
  });

  it("gives shorts their height and no fullscreen", () => {
    expect(defaultsFor("shorts", 7)).toEqual({ type: "shorts", id: 7, height: "700px", fullscreen: false });
    expect(DEFAULT_SHORTS_HEIGHT).toBe("700px");
  });
});

describe("readValue", () => {
  it("reads an object and fills the defaults of its type", () => {
    expect(readValue({ type: "clip", id: 12 })).toEqual(defaultsFor("clip", 12));
  });

  it("keeps what the editor stored over the defaults", () => {
    expect(readValue({ type: "clip", id: 12, showTranscript: false, playout: "wide" })).toEqual({
      type: "clip", id: 12, showTranscript: false, showChapters: false, lazyLoad: true, playout: "wide",
    });
  });

  it("reads a JSON string", () => {
    expect(readValue('{"type":"channel","id":"5","height":"400px"}')).toEqual({ type: "channel", id: 5, height: "400px", lazyLoad: false });
  });

  it("turns a numeric string id into a number", () => {
    expect(readValue({ type: "shorts", id: "9" })?.id).toBe(9);
  });

  it.each([
    ["nothing", undefined],
    ["null", null],
    ["an empty string", ""],
    ["text that is not JSON", "{nope"],
    ["JSON that is not an object", "42"],
    ["a number", 5],
    ["a missing id", { type: "clip" }],
    ["id zero", { type: "clip", id: 0 }],
    ["a negative id", { type: "clip", id: -3 }],
    ["a fractional id", { type: "clip", id: 1.5 }],
    ["a text id", { type: "clip", id: "abc" }],
    ["an unknown type", { type: "playlist", id: 4 }],
    ["a missing type", { id: 4 }],
  ])("treats %s as no value", (_name, raw) => {
    expect(readValue(raw)).toBeUndefined();
  });
});

describe("selectionChanged", () => {
  const clip = (id: number) => defaultsFor("clip", id);

  it("is true when the selection is cleared, set, or another video", () => {
    expect(selectionChanged(clip(1), undefined)).toBe(true);
    expect(selectionChanged(undefined, clip(1))).toBe(true);
    expect(selectionChanged(clip(1), clip(2))).toBe(true);
    expect(selectionChanged(clip(1), defaultsFor("channel", 1))).toBe(true);
  });

  it("is false for the same video with another option, or for nothing both times", () => {
    expect(selectionChanged(clip(1), { ...clip(1), playout: "x" })).toBe(false);
    expect(selectionChanged(undefined, undefined)).toBe(false);
  });
});
