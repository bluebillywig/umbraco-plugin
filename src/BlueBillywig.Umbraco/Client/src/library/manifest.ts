/*
 * Why a section with section views, and not a tree with workspaces.
 *
 * Umbraco's own Media section is a tree plus workspaces: right for things Umbraco stores itself, where every item
 * has an id in Umbraco's database, its own route, permissions per node, bulk actions and a recycle bin. The Blue
 * Billywig library is none of that. The videos live in an external service, so there is no entity or tree API to
 * back a tree, and the only operations are browse, pick, upload and delete. For a browser like that, Umbraco's
 * extension guidance for a custom section is a `section` whose content is `sectionView`s (the tabs in the section
 * header). Clip details are not a route of their own either: they open as a sidebar `modal`, the same way
 * Umbraco opens its own pickers, which also lets the property editor reuse the browser as a picker modal.
 *
 * Access: the section is granted per user group (Users > User Groups > Sections). The server enforces the same
 * rule, and delete additionally needs one of the configured user groups (admin and editor by default).
 */
const SECTION = "BlueBillywig.Section";

const inSection = [{ alias: "Umb.Condition.SectionAlias", match: SECTION }];

export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "section",
    alias: SECTION,
    name: "Blue Billywig section",
    weight: 400,
    meta: { label: "Blue Billywig", pathname: "blue-billywig" },
    conditions: [{ alias: "Umb.Condition.SectionUserPermission", match: SECTION }],
  },
  {
    type: "sectionView",
    alias: "BlueBillywig.SectionView.Videos",
    name: "Blue Billywig videos",
    element: () => import("./library-videos.element.js"),
    weight: 400,
    meta: { label: "#bluebillywig_tabVideos", pathname: "videos", icon: "icon-movie-alt" },
    conditions: inSection,
  },
  {
    type: "sectionView",
    alias: "BlueBillywig.SectionView.Livestreams",
    name: "Blue Billywig livestreams",
    element: () => import("./library-livestreams.element.js"),
    weight: 300,
    meta: { label: "#bluebillywig_tabLivestreams", pathname: "livestreams", icon: "icon-radio" },
    conditions: inSection,
  },
  {
    type: "sectionView",
    alias: "BlueBillywig.SectionView.Playlists",
    name: "Blue Billywig playlists",
    element: () => import("./library-playlists.element.js"),
    weight: 200,
    meta: { label: "#bluebillywig_tabPlaylists", pathname: "playlists", icon: "icon-playlist" },
    conditions: inSection,
  },
  {
    type: "sectionView",
    alias: "BlueBillywig.SectionView.Upload",
    name: "Blue Billywig upload",
    element: () => import("./library-upload.element.js"),
    weight: 100,
    meta: { label: "#bluebillywig_tabUpload", pathname: "upload", icon: "icon-cloud-upload" },
    conditions: inSection,
  },
  {
    type: "sectionView",
    alias: "BlueBillywig.SectionView.Platform",
    name: "Blue Billywig platform",
    element: () => import("./library-platform.element.js"),
    weight: 50,
    meta: { label: "#bluebillywig_tabPlatform", pathname: "platform", icon: "icon-globe" },
    conditions: inSection,
  },
  {
    type: "modal",
    alias: "BlueBillywig.Modal.ClipDetails",
    name: "Blue Billywig clip details",
    element: () => import("./clip-details.modal.element.js"),
  },
  {
    type: "modal",
    alias: "BlueBillywig.Modal.ClipEdit",
    name: "Blue Billywig clip editor",
    element: () => import("./clip-edit.modal.element.js"),
  },
  {
    type: "modal",
    alias: "BlueBillywig.Modal.LibraryPicker",
    name: "Blue Billywig library picker",
    element: () => import("./library-picker.modal.element.js"),
  },
];
