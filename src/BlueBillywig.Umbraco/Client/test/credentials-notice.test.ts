import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import * as notice from "../src/shared/credentials-notice";
import { isSettingsPath, SETTINGS_PATH, shouldShowNotice } from "../src/shared/credentials-notice";

describe("shouldShowNotice", () => {
  it("shows when credentials are missing, off the settings dashboard, and not dismissed", () => {
    expect(shouldShowNotice({ configured: false, onSettings: false, dismissed: false })).toBe(true);
  });
  it("shows nothing when configured or when the status is unknown", () => {
    expect(shouldShowNotice({ configured: true, onSettings: false, dismissed: false })).toBe(false);
    expect(shouldShowNotice({ configured: undefined, onSettings: false, dismissed: false })).toBe(false);
  });
  it("is hidden on the settings dashboard and once dismissed", () => {
    expect(shouldShowNotice({ configured: false, onSettings: true, dismissed: false })).toBe(false);
    expect(shouldShowNotice({ configured: false, onSettings: false, dismissed: true })).toBe(false);
  });
});

describe("isSettingsPath", () => {
  it("matches the settings dashboard, with or without a trailing slash", () => {
    expect(isSettingsPath(SETTINGS_PATH)).toBe(true);
    expect(isSettingsPath(SETTINGS_PATH + "/")).toBe(true);
  });
  it("does not match other backoffice pages", () => {
    expect(isSettingsPath("/umbraco/section/content")).toBe(false);
    expect(isSettingsPath("/umbraco/section/settings/dashboard/blue-billywig-analytics")).toBe(false);
  });
});

describe("dismissal lasts for the current page load only", () => {
  const read = (path: string) => readFileSync(fileURLToPath(new URL(path, import.meta.url)), "utf8");

  it("offers no storage-backed dismissal", () => {
    expect(Object.keys(notice).sort()).toEqual(["SETTINGS_PATH", "isSettingsPath", "shouldShowNotice"]);
  });
  it("neither the rules nor the element touch localStorage or sessionStorage", () => {
    for (const file of ["../src/shared/credentials-notice.ts", "../src/shared/credentials-header-app.element.ts"]) {
      const code = read(file).replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
      expect(code, file).not.toMatch(/localStorage|sessionStorage|indexedDB|document\.cookie/);
    }
  });
  it("keeps the dismissal in element state, so a new page load shows the notice again", () => {
    const element = read("../src/shared/credentials-header-app.element.ts");
    expect(element).toMatch(/@state\(\) private _dismissed = false;/);
    expect(element).toMatch(/this\._dismissed = true;/);
  });
});
