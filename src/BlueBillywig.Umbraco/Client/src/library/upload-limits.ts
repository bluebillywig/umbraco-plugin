import { englishTerm, type Term } from "../localization/terms.js";

/**
 * What the upload view accepts. The same numbers as the reference plugin's upload script: one file per upload (the
 * title, description, tags and status fields describe a single clip), at most 20 GB, and these audio and video types.
 * The server enforces the size again in `UploadValidation.MaxFileSize`; a test keeps the two equal.
 */
export const MAX_FILE_SIZE = 21_474_836_480;
export const ALLOWED_TYPES = [".mp4", ".mov", ".avi", ".webm", ".ogg", ".mxf", ".mpg", ".mpeg", ".mkv", ".m4v", ".flv", ".wmv", ".mp3", ".wav", ".aac", ".flac", ".m4a"];

/** The sentence shown above the file area, built from the constants so it cannot drift from what is enforced. */
export function limitsText(t: Term = englishTerm): string {
  return t("bluebillywig_uploadLimits", ALLOWED_TYPES.join(", "), MAX_FILE_SIZE / 1024 ** 3);
}
