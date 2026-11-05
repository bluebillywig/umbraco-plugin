import { describe, expect, it } from "vitest";
import { actionRequest, actionTook, applySaved, clipBanner, embargoText } from "../src/embed/clip-status";

const NOW = Date.parse("2030-06-01T12:00:00Z");

describe("clipBanner", () => {
  it("says nothing for a published clip, or no clip", () => {
    expect(clipBanner(undefined, NOW)).toBeUndefined();
    expect(clipBanner({ status: "published" }, NOW)).toBeUndefined();
    expect(clipBanner({ status: null }, NOW)).toBeUndefined();
  });

  it("offers Publish now for a draft and for an archived clip", () => {
    expect(clipBanner({ status: "draft" }, NOW)).toEqual({ kind: "draft", embargo: undefined, actions: ["publish"] });
    expect(clipBanner({ status: "archived" }, NOW)).toEqual({ kind: "archived", embargo: undefined, actions: ["publish"] });
  });

  it("offers Remove embargo while now lies inside the window", () => {
    const banner = clipBanner({ status: "published", embargoFrom: "2030-05-01T00:00:00Z", embargoTo: "2030-07-01T00:00:00Z" }, NOW);
    expect(banner).toEqual({ kind: "embargo", embargo: { from: "2030-05-01T00:00:00Z", to: "2030-07-01T00:00:00Z" }, actions: ["removeEmbargo"] });
  });

  it("ignores a window that has not started or has ended", () => {
    expect(clipBanner({ status: "published", embargoFrom: "2030-07-01T00:00:00Z", embargoTo: "2030-08-01T00:00:00Z" }, NOW)).toBeUndefined();
    expect(clipBanner({ status: "published", embargoFrom: "2030-01-01T00:00:00Z", embargoTo: "2030-02-01T00:00:00Z" }, NOW)).toBeUndefined();
  });

  it("compares times, not strings: the same instant written in two zones", () => {
    // 12:00Z is 14:00+02:00; as strings "2030-06-01T13:00:00+02:00" sorts before "2030-06-01T12:00:00Z" but is after it in time.
    expect(clipBanner({ embargoFrom: "2030-06-01T13:30:00+02:00", embargoTo: "2030-06-01T15:00:00+02:00" }, NOW)).toBeDefined();
    expect(clipBanner({ embargoFrom: "2030-06-01T13:30:00+02:00", embargoTo: "2030-06-01T13:59:00+02:00" }, NOW)).toBeUndefined();
  });

  it("needs both ends to be dates", () => {
    expect(clipBanner({ embargoFrom: "2030-05-01T00:00:00Z" }, NOW)).toBeUndefined();
    expect(clipBanner({ embargoFrom: "later", embargoTo: "2030-07-01T00:00:00Z" }, NOW)).toBeUndefined();
    expect(clipBanner({ embargoFrom: null, embargoTo: null }, NOW)).toBeUndefined();
  });

  it("keeps the draft sentence for a draft under embargo and offers both actions", () => {
    const banner = clipBanner({ status: "draft", embargoFrom: "2030-05-01T00:00:00Z", embargoTo: "2030-07-01T00:00:00Z" }, NOW);
    expect(banner?.kind).toBe("draft");
    expect(banner?.actions).toEqual(["publish", "removeEmbargo"]);
  });
});

describe("actions", () => {
  it("sends only the status for Publish now, and only the two empty dates for Remove embargo", () => {
    expect(actionRequest("publish")).toEqual({ status: "published" });
    expect(actionRequest("removeEmbargo")).toEqual({ embargoFrom: "", embargoTo: "" });
  });

  it("claims success only when the saved clip shows the change", () => {
    expect(actionTook("publish", { status: "published" })).toBe(true);
    expect(actionTook("publish", { status: "draft" })).toBe(false);
    expect(actionTook("removeEmbargo", { embargoFrom: null, embargoTo: null })).toBe(true);
    expect(actionTook("removeEmbargo", { embargoFrom: "", embargoTo: "" })).toBe(true);
    // The platform kept the dates (bb.dev ignores empty ones): no "Embargo removed.".
    expect(actionTook("removeEmbargo", { embargoFrom: "2030-05-01T00:00:00Z", embargoTo: "2030-07-01T00:00:00Z" })).toBe(false);
    expect(actionTook("removeEmbargo", undefined)).toBe(false);
  });

  it("patches the held clip with what the server reports and leaves the rest alone", () => {
    const clip = { id: 5, title: "t", status: "draft", embargoFrom: "a", embargoTo: "b" };
    expect(applySaved(clip, "publish", { status: "published" })).toEqual({ ...clip, status: "published" });
    expect(applySaved(clip, "removeEmbargo", { embargoFrom: null, embargoTo: undefined })).toEqual({ ...clip, embargoFrom: null, embargoTo: null });
    expect(clip.status).toBe("draft");
  });

  it("writes an embargo date the way the reference plugin does", () => {
    expect(embargoText("2030-05-01T09:30:00Z")).toBe("2030-05-01 09:30");
  });

  it("shows the banner times in UTC, whatever zone the date carries", () => {
    expect(embargoText("2030-05-01T11:30:00+02:00")).toBe("2030-05-01 09:30");
    expect(embargoText("not a date")).toBe("not a date");
  });
});
