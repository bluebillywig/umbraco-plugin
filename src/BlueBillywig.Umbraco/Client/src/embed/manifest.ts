export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "propertyEditorSchema",
    alias: "BlueBillywig.Video",
    name: "Blue Billywig Video",
    meta: {
      defaultPropertyEditorUiAlias: "BlueBillywig.PropertyEditorUi.Video",
    },
  },
  {
    type: "propertyEditorUi",
    alias: "BlueBillywig.PropertyEditorUi.Video",
    name: "Blue Billywig Video",
    js: () => import("./video-picker.element.js"),
    meta: {
      label: "Blue Billywig Video",
      icon: "icon-movie-alt",
      group: "media",
      propertyEditorSchemaAlias: "BlueBillywig.Video",
    },
  },
];
