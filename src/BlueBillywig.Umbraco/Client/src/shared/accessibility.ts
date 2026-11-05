/**
 * Small pure helpers for keyboard and motion behaviour, kept apart from the Lit elements so they can be read (and
 * checked from the browser console) without a rendered element. Vitest tests in Client/test/ cover them.
 */

/**
 * The tab to move to for a key press on a tab list, or undefined when the key does nothing. Left and right wrap,
 * Home and End jump to the ends: the arrow-key pattern the reference plugin's library tabs follow.
 */
export function tabTarget(key: string, current: number, count: number): number | undefined {
  if (count <= 0) return undefined;
  switch (key) {
    case "ArrowRight":
    case "ArrowDown":
      return (current + 1) % count;
    case "ArrowLeft":
    case "ArrowUp":
      return (current - 1 + count) % count;
    case "Home":
      return 0;
    case "End":
      return count - 1;
    default:
      return undefined;
  }
}

/**
 * Whether a tile may start its moving-thumbnail preview on hover. Not for touch devices (no hover), and not for people
 * who asked for reduced motion: the reference plugin's hover preview skips both.
 */
export function hoverPreviewAllowed(matches: (query: string) => boolean): boolean {
  return !matches("(hover: none)") && !matches("(prefers-reduced-motion: reduce)");
}
