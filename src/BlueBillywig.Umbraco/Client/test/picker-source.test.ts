import { describe, expect, it } from "vitest";
import { hasLiveStreams, isRecentList, listHeadingKey, pickerSourceType, selectFolder, selectLive } from "../src/library/picker-source";

const NONE = { search: "", folder: "", liveOnly: false, mediaType: "all", from: "", to: "" };

describe("live streams in the picker", () => {
  it("asks for the live source type only while the toggle is on", () => {
    expect(pickerSourceType(true)).toBe("live");
    expect(pickerSourceType(false)).toBeUndefined();
  });

  it("offers the toggle only when there is at least one live stream", () => {
    expect(hasLiveStreams({ total: 3 }, undefined)).toBe(true);
    expect(hasLiveStreams({ total: 0 }, undefined)).toBe(false);
    expect(hasLiveStreams(undefined, undefined)).toBe(false);
  });

  it("hides the toggle when the check failed", () => {
    expect(hasLiveStreams({ total: 3 }, { status: 502 })).toBe(false);
    expect(hasLiveStreams(undefined, new Error("x"))).toBe(false);
  });

  it("clears the folder when live is turned on, and leaves live when a folder is picked", () => {
    expect(selectLive(true)).toEqual({ folder: "", liveOnly: true });
    expect(selectLive(false)).toEqual({ folder: "", liveOnly: false });
    expect(selectFolder("abc")).toEqual({ folder: "abc", liveOnly: false });
  });
});

describe("recent videos", () => {
  it("is the unfiltered first view of the library", () => {
    expect(isRecentList(NONE)).toBe(true);
    expect(isRecentList({ ...NONE, mediaType: "" })).toBe(true);
  });

  it.each([
    { search: "cats" },
    { folder: "abc" },
    { liveOnly: true },
    { mediaType: "audio" },
    { from: "2030-01-01" },
    { to: "2030-01-01" },
  ])("is not recent once %o narrows it", (narrow) => {
    expect(isRecentList({ ...NONE, ...narrow })).toBe(false);
  });
});

describe("list heading", () => {
  it("says Live streams for the toggle alone and Search results once a search is typed", () => {
    expect(listHeadingKey({ ...NONE, liveOnly: true })).toBe("bluebillywig_liveStreams");
    expect(listHeadingKey({ ...NONE, liveOnly: true, search: "cats" })).toBe("bluebillywig_searchResults");
    expect(listHeadingKey({ ...NONE, liveOnly: true, search: "   " })).toBe("bluebillywig_liveStreams");
  });

  it("says Recent videos for the unfiltered list and Search results for any other narrowing", () => {
    expect(listHeadingKey(NONE)).toBe("bluebillywig_recentVideos");
    expect(listHeadingKey({ ...NONE, search: "cats" })).toBe("bluebillywig_searchResults");
    expect(listHeadingKey({ ...NONE, folder: "abc" })).toBe("bluebillywig_searchResults");
  });
});
