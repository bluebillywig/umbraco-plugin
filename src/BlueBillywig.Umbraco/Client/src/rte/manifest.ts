/*
 * Rich text insertion of a library video (E4). Umbraco's rich text editor (Tiptap) takes toolbar buttons as
 * `tiptapToolbarExtension` manifests of kind `button`, whose `api` runs when the button is pressed. This one belongs to
 * the Embed extension, like Umbraco's own Embed button, and reuses its `setEmbeddedMedia` command, so the stored markup is
 * the one the oEmbed route already stores and renders.
 *
 * To use it: Settings > Data Types > the Rich Text Editor data type > Extensions: enable "Embed" > Toolbar: add
 * "Blue Billywig video".
 */
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "tiptapToolbarExtension",
    kind: "button",
    alias: "BlueBillywig.Tiptap.Toolbar.Video",
    name: "Blue Billywig video toolbar button",
    api: () => import("./video-toolbar.api.js"),
    forExtensions: ["Umb.Tiptap.Embed"],
    meta: {
      alias: "bluebillywigVideo",
      icon: "icon-movie-alt",
      label: "Blue Billywig video",
    },
  },
];
