export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "dashboard",
    alias: "BlueBillywig.Dashboard.Settings",
    name: "Blue Billywig settings",
    js: () => import("./settings.element.js"),
    weight: -10,
    meta: { label: "Blue Billywig", pathname: "blue-billywig" },
    conditions: [{ alias: "Umb.Condition.SectionAlias", match: "Umb.Section.Settings" }],
  },
  {
    // The plugin's dashboard widget. Umbraco's Content section opens on its dashboards, so this is where editors see it.
    type: "dashboard",
    alias: "BlueBillywig.Dashboard.Analytics",
    name: "Blue Billywig analytics",
    js: () => import("./analytics.element.js"),
    weight: -5,
    meta: { label: "Blue Billywig analytics", pathname: "blue-billywig-analytics" },
    conditions: [{ alias: "Umb.Condition.SectionAlias", match: "Umb.Section.Content" }],
  },
  {
    // S9: the "not configured yet" notice in every section, for administrators only. A header app is the documented place for
    // something that stays visible whatever section is open.
    type: "headerApp",
    alias: "BlueBillywig.HeaderApp.CredentialsNotice",
    name: "Blue Billywig credentials notice",
    js: () => import("../shared/credentials-header-app.element.js"),
    weight: 900,
    conditions: [{ alias: "Umb.Condition.CurrentUser.IsAdmin" }],
  },
];
