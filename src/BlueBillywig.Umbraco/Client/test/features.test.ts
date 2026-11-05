import { describe, expect, it } from "vitest";
import { offeredTypes, readFeatures } from "../src/embed/features";

const ALL = ["clip", "channel", "shorts"] as const;

describe("offeredTypes", () => {
  it("offers only the types the publication has", () => {
    expect(offeredTypes(ALL, { hasChannels: true, hasShorts: false })).toEqual(["clip", "channel"]);
    expect(offeredTypes(ALL, { hasChannels: false, hasShorts: true })).toEqual(["clip", "shorts"]);
    expect(offeredTypes(ALL, { hasChannels: false, hasShorts: false })).toEqual(["clip"]);
  });

  it("offers every type while the answer is unknown or failed", () => {
    expect(offeredTypes(ALL, undefined)).toEqual(["clip", "channel", "shorts"]);
  });

  it("keeps the type of the stored value, so an existing embed is never stranded", () => {
    expect(offeredTypes(ALL, { hasChannels: false, hasShorts: false }, "channel")).toEqual(["clip", "channel"]);
    expect(offeredTypes(ALL, { hasChannels: false, hasShorts: false }, "shorts")).toEqual(["clip", "shorts"]);
  });
});

describe("readFeatures", () => {
  it("reads the answer", () => {
    expect(readFeatures({ hasChannels: true, hasShorts: false }, undefined)).toEqual({ hasChannels: true, hasShorts: false });
  });

  it("is undefined, which fails open, for an error, no data or an unexpected shape", () => {
    expect(readFeatures(undefined, { status: 502 })).toBeUndefined();
    expect(readFeatures({ hasChannels: true, hasShorts: false }, new Error("x"))).toBeUndefined();
    expect(readFeatures(undefined, undefined)).toBeUndefined();
    expect(readFeatures({ hasChannels: "yes" }, undefined)).toBeUndefined();
    expect(readFeatures("x", undefined)).toBeUndefined();
  });
});
