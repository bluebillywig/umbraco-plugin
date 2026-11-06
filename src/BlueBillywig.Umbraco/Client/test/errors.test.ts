import { describe, expect, it } from "vitest";
import { defaultApiAddress, errorText, formatDate, formatDuration, libraryError, saveError, statusOf } from "../src/library/errors";

describe("statusOf", () => {
  it("reads the HTTP status of a failed call", () => {
    expect(statusOf({ status: 502 })).toBe(502);
  });

  it("is 0 when there was no answer or the status is not a number", () => {
    expect(statusOf(undefined)).toBe(0);
    expect(statusOf(new Error("network"))).toBe(0);
    expect(statusOf({ status: "502" })).toBe(0);
  });
});

describe("libraryError", () => {
  it("says out loud that Blue Billywig failed on a 502, so it is never mistaken for an empty library", () => {
    const text = libraryError({ status: 502 }, "videos");
    expect(text).toContain("Blue Billywig did not answer");
    expect(text).toContain("the videos could not be loaded");
  });

  it("says there is no access on 401 and 403", () => {
    expect(libraryError({ status: 401 }, "videos")).toBe("You do not have access to the videos.");
    expect(libraryError({ status: 403 }, "videos")).toBe("You do not have access to the videos.");
  });

  it("says the request was invalid on 400", () => {
    expect(libraryError({ status: 400 }, "playlists")).toBe("The request for the playlists was not valid.");
  });

  it("falls back to a plain sentence", () => {
    expect(libraryError({ status: 500 }, "videos")).toBe("The videos could not be loaded.");
    expect(libraryError(undefined, "videos")).toBe("The videos could not be loaded.");
  });
});

describe("saveError", () => {
  it("never shows upstream text for a 502, even when the error carries a detail", () => {
    const text = saveError({ status: 502, problemDetails: { detail: "upstream secret body" } });
    expect(text).toContain("Blue Billywig did not accept the change");
    expect(text).not.toContain("upstream secret body");
  });

  it("uses the what-word in the sentences", () => {
    expect(saveError({ status: 502 }, "thumbnail")).toContain("did not accept the thumbnail");
    expect(saveError({ status: 403 }, "thumbnail")).toBe("You do not have permission to make this thumbnail.");
    expect(saveError(undefined, "thumbnail")).toBe("The thumbnail could not be saved.");
  });

  it("says the clip is gone on 404", () => {
    expect(saveError({ status: 404 })).toBe("This clip no longer exists.");
  });

  it.each([400, 409, 422])("shows the server's own sentence on %i", (status) => {
    expect(saveError({ status, problemDetails: { detail: "Invalid status value" } })).toBe("Invalid status value");
  });

  it("does not show a detail that is empty, not text, or on another status", () => {
    expect(saveError({ status: 400, problemDetails: { detail: "" } })).toBe("The change could not be saved.");
    expect(saveError({ status: 400, problemDetails: { detail: 5 } })).toBe("The change could not be saved.");
    expect(saveError({ status: 500, problemDetails: { detail: "internal" } })).toBe("The change could not be saved.");
  });
});

describe("formatDuration", () => {
  it("is empty for nothing, zero or negative", () => {
    expect(formatDuration(undefined)).toBe("");
    expect(formatDuration(null)).toBe("");
    expect(formatDuration(0)).toBe("");
    expect(formatDuration(-4)).toBe("");
  });

  it("shows minutes and seconds under an hour", () => {
    expect(formatDuration(5)).toBe("0:05");
    expect(formatDuration(90)).toBe("1:30");
    expect(formatDuration(3599)).toBe("59:59");
  });

  it("shows hours, padded minutes and seconds from an hour", () => {
    expect(formatDuration(3600)).toBe("1:00:00");
    expect(formatDuration(3725.9)).toBe("1:02:05");
  });
});

describe("formatDate", () => {
  it("is empty for nothing or an unparsable date", () => {
    expect(formatDate(undefined)).toBe("");
    expect(formatDate(null)).toBe("");
    expect(formatDate("not a date")).toBe("");
  });

  it("formats a real date as local text", () => {
    expect(formatDate("2026-11-04T10:00:00Z")).toBe(new Date("2026-11-04T10:00:00Z").toLocaleString());
  });
});

describe("errorText", () => {
  it("prefers the error extension, then detail, then the first of errors", () => {
    expect(errorText({ problemDetails: { error: "A", detail: "B", title: "Bad Request" } })).toBe("A");
    expect(errorText({ problemDetails: { detail: "B", title: "Bad Request" } })).toBe("B");
    expect(errorText({ problemDetails: { errors: ["C", "D"], title: "Bad Request" } })).toBe("C");
    expect(errorText({ problemDetails: { errors: { request: ["E"] }, title: "x" } })).toBe("E");
  });

  it("uses the status title only as a last resort and is undefined for nothing", () => {
    expect(errorText({ message: "Network down", problemDetails: { title: "Bad Request" } })).toBe("Network down");
    expect(errorText({ problemDetails: { title: "Bad Request" } })).toBe("Bad Request");
    expect(errorText(undefined)).toBeUndefined();
    expect(errorText({ problemDetails: { error: "  " } })).toBeUndefined();
  });

  it("skips blank and non-string values", () => {
    expect(errorText({ problemDetails: { error: 5, detail: "" }, error: "X" })).toBe("X");
  });
});

describe("defaultApiAddress", () => {
  it("shows the address for a valid publication", () => {
    expect(defaultApiAddress("my-publication")).toBe("https://my-publication.bbvms.com");
    expect(defaultApiAddress("a.b")).toBe("https://a.b.bbvms.com");
  });

  it("shows nothing until the publication is valid", () => {
    for (const bad of [undefined, null, "", "ab", "bad host/../x!", "Upper", "a..b", "-ab", "ab-", "x".repeat(64)]) {
      expect(defaultApiAddress(bad)).toBe("");
    }
  });
});
