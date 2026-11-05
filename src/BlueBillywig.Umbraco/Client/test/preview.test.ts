import { describe, expect, it } from "vitest";
import { defaultsFor } from "../src/embed/video-value";
import { PREVIEW_ALLOW, PREVIEW_REFERRER_POLICY, PREVIEW_SANDBOX, previewHeight, previewKey, previewQuery } from "../src/embed/preview";

describe("previewQuery", () => {
  it("sends the playout only for a clip, and the publication only when the value has one", () => {
    expect(previewQuery({ ...defaultsFor("clip", 7), playout: "wide" })).toEqual({ id: 7, type: "clip", playout: "wide" });
    expect(previewQuery({ ...defaultsFor("channel", 7), playout: "wide" })).toEqual({ id: 7, type: "channel" });
    expect(previewQuery({ ...defaultsFor("shorts", 7), publication: "other" })).toEqual({ id: 7, type: "shorts", publication: "other" });
  });

  it("never carries a token or any option that does not change the player URL", () => {
    const keys = Object.keys(previewQuery({ ...defaultsFor("clip", 7), showTranscript: false, lazyLoad: false, playout: "p" }));
    expect(keys.sort()).toEqual(["id", "playout", "type"]);
  });
});

describe("previewKey", () => {
  it("changes with the id, type, playout and publication, so a loaded frame is dropped", () => {
    const base = previewKey(defaultsFor("clip", 7));
    expect(previewKey(defaultsFor("clip", 8))).not.toBe(base);
    expect(previewKey(defaultsFor("channel", 7))).not.toBe(base);
    expect(previewKey({ ...defaultsFor("clip", 7), playout: "wide" })).not.toBe(base);
    expect(previewKey({ ...defaultsFor("clip", 7), publication: "other" })).not.toBe(base);
  });

  it("does not change with options that leave the player URL as it is", () => {
    expect(previewKey({ ...defaultsFor("clip", 7), showChapters: true, lazyLoad: false })).toBe(previewKey(defaultsFor("clip", 7)));
    expect(previewKey({ ...defaultsFor("channel", 7), height: "300px" })).toBe(previewKey(defaultsFor("channel", 7)));
  });

  it("is empty without a value", () => {
    expect(previewKey(undefined)).toBe("");
  });
});

describe("the frame", () => {
  it("is sandboxed, without a referrer, and cannot navigate the backoffice", () => {
    expect(PREVIEW_REFERRER_POLICY).toBe("no-referrer");
    expect(PREVIEW_SANDBOX.split(" ")).toEqual(["allow-scripts", "allow-same-origin", "allow-presentation", "allow-popups"]);
    expect(PREVIEW_SANDBOX).not.toContain("allow-top-navigation");
    expect(PREVIEW_ALLOW).toBe("autoplay; fullscreen");
  });

  it("uses a 16:9 box for a clip and the configured height for a channel or shorts", () => {
    expect(previewHeight(defaultsFor("clip", 1))).toBeUndefined();
    expect(previewHeight(defaultsFor("channel", 1))).toBe("600px");
    expect(previewHeight({ ...defaultsFor("shorts", 1), height: "80vh" })).toBe("80vh");
  });

  it("falls back to the default height for a value that is not a CSS length", () => {
    expect(previewHeight({ ...defaultsFor("channel", 1), height: "1px;background:url(x)" })).toBe("600px");
    expect(previewHeight({ ...defaultsFor("shorts", 1), height: "" })).toBe("700px");
  });
});
