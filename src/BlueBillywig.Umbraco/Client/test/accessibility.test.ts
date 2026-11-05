import { describe, expect, it } from "vitest";
import { hoverPreviewAllowed, tabTarget } from "../src/shared/accessibility";

describe("hoverPreviewAllowed", () => {
  const media = (active: string[]) => (query: string) => active.includes(query);

  it("allows the moving preview when the device hovers and motion is not reduced", () => {
    expect(hoverPreviewAllowed(media([]))).toBe(true);
  });

  it("refuses it on a touch device (no hover)", () => {
    expect(hoverPreviewAllowed(media(["(hover: none)"]))).toBe(false);
  });

  it("refuses it when the person asked for reduced motion", () => {
    expect(hoverPreviewAllowed(media(["(prefers-reduced-motion: reduce)"]))).toBe(false);
  });

  it("refuses it when both apply", () => {
    expect(hoverPreviewAllowed(media(["(hover: none)", "(prefers-reduced-motion: reduce)"]))).toBe(false);
  });
});

describe("tabTarget", () => {
  it("moves right and down by one and wraps from the last tab to the first", () => {
    expect(tabTarget("ArrowRight", 0, 3)).toBe(1);
    expect(tabTarget("ArrowDown", 1, 3)).toBe(2);
    expect(tabTarget("ArrowRight", 2, 3)).toBe(0);
  });

  it("moves left and up by one and wraps from the first tab to the last", () => {
    expect(tabTarget("ArrowLeft", 2, 3)).toBe(1);
    expect(tabTarget("ArrowUp", 1, 3)).toBe(0);
    expect(tabTarget("ArrowLeft", 0, 3)).toBe(2);
  });

  it("jumps to the ends with Home and End", () => {
    expect(tabTarget("Home", 2, 5)).toBe(0);
    expect(tabTarget("End", 1, 5)).toBe(4);
  });

  it("does nothing for other keys or when there are no tabs", () => {
    expect(tabTarget("Enter", 1, 3)).toBeUndefined();
    expect(tabTarget("a", 1, 3)).toBeUndefined();
    expect(tabTarget("ArrowRight", 0, 0)).toBeUndefined();
    expect(tabTarget("Home", 0, -1)).toBeUndefined();
  });

  it("stays on the only tab", () => {
    expect(tabTarget("ArrowRight", 0, 1)).toBe(0);
    expect(tabTarget("ArrowLeft", 0, 1)).toBe(0);
  });
});
