import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { ALLOWED_TYPES, limitsText, MAX_FILE_SIZE } from "../src/library/upload-limits";

// The server half of the limit. Read as text and parsed, the way the C# tests read upload-limits.ts in the other direction.
const serverSource = readFileSync(
  fileURLToPath(new URL("../../Upload/UploadValidation.cs", import.meta.url)),
  "utf8",
);

describe("upload limits", () => {
  it("allow exactly the file size the server enforces", () => {
    const match = /public const long MaxFileSize = ([\d_]+);/.exec(serverSource);
    expect(match, "MaxFileSize not found in UploadValidation.cs").not.toBeNull();
    expect(MAX_FILE_SIZE).toBe(Number(match![1].replaceAll("_", "")));
  });

  it("are 20 GB", () => {
    expect(MAX_FILE_SIZE).toBe(20 * 1024 ** 3);
  });

  it("accept the reference plugin's audio and video types, in order", () => {
    expect(ALLOWED_TYPES).toEqual([
      ".mp4", ".mov", ".avi", ".webm", ".ogg", ".mxf", ".mpg", ".mpeg", ".mkv", ".m4v", ".flv", ".wmv",
      ".mp3", ".wav", ".aac", ".flac", ".m4a",
    ]);
  });

  it("are lower-case extensions with a dot and no duplicates (the server has no extension list to compare with)", () => {
    for (const type of ALLOWED_TYPES) expect(type).toMatch(/^\.[a-z0-9]+$/);
    expect(new Set(ALLOWED_TYPES).size).toBe(ALLOWED_TYPES.length);
  });

  it("are the words shown above the file area, built from the same constants", () => {
    const text = limitsText();
    expect(text).toContain("up to 20 GB");
    expect(text).toContain("One file at a time");
    for (const type of ALLOWED_TYPES) expect(text).toContain(type);
  });
});
