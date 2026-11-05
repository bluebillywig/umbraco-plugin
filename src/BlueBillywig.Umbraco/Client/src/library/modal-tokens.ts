import { UmbModalToken } from "@umbraco-cms/backoffice/modal";

export interface ClipDetailsModalData {
  clipId: number;
}

export interface ClipDetailsModalValue {
  /** True when the clip was deleted from the modal, so the list can reload. */
  deleted?: boolean;
  /** True when the clip was edited from the modal, so the list shows the new title, tags and thumbnail. */
  changed?: boolean;
}

export const BB_CLIP_DETAILS_MODAL = new UmbModalToken<ClipDetailsModalData, ClipDetailsModalValue>("BlueBillywig.Modal.ClipDetails", {
  modal: { type: "sidebar", size: "medium" },
});

export interface LibraryPickerModalValue {
  id: number;
  title: string;
  thumbnailUrl?: string | null;
  status?: string | null;
  length?: number | null;
}

export const BB_LIBRARY_PICKER_MODAL = new UmbModalToken<object, LibraryPickerModalValue>("BlueBillywig.Modal.LibraryPicker", {
  modal: { type: "sidebar", size: "full" },
});

export interface ClipEditModalData {
  clipId: number;
}

export interface ClipEditModalValue {
  /** True when anything was saved, so the caller can reload what it shows. */
  changed?: boolean;
}

export const BB_CLIP_EDIT_MODAL = new UmbModalToken<ClipEditModalData, ClipEditModalValue>("BlueBillywig.Modal.ClipEdit", {
  modal: { type: "sidebar", size: "large" },
});
