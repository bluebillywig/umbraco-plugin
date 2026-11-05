import { UmbTiptapToolbarElementApiBase } from "@umbraco-cms/backoffice/tiptap";
import type { Editor } from "@umbraco-cms/backoffice/tiptap";
import { umbOpenModal } from "@umbraco-cms/backoffice/modal";
import { tryExecute } from "@umbraco-cms/backoffice/resources";
import { getStatus } from "../api/index.js";
import { BB_LIBRARY_PICKER_MODAL } from "../library/modal-tokens.js";

/**
 * The rich text toolbar button "Blue Billywig video": the counterpart of the plugin's classic editor media button. It opens
 * the library browser, and inserts what Umbraco's own Embed button inserts for a pasted video URL: an
 * <code>umb-embed-holder</code> that stores the reference (the URL) and a line of inert text. The player is made when the
 * page is rendered, per request, by <code>RichTextEmbedRewriter</code>, so consent, the CSP nonce and escaping apply to
 * this embed as they do to a pasted one (rows E7 and E13).
 *
 * It is a toolbar extension of the Embed extension (<code>Umb.Tiptap.Embed</code>), so a data type shows the button only
 * when its rich text editor has "Embed" enabled and "Blue Billywig video" added to the toolbar.
 */
export default class BbVideoToolbarApi extends UmbTiptapToolbarElementApiBase {
  override async execute(editor?: Editor) {
    if (!editor) return;
    const picked = await umbOpenModal(this, BB_LIBRARY_PICKER_MODAL, { data: {} }).catch(() => undefined);
    if (!picked) return;

    // The reference names the publication, which only the server knows for certain.
    const { data: status } = await tryExecute(this, getStatus());
    if (!status?.publication) return;
    const playout = /^[A-Za-z0-9_-]{1,64}$/.test(status.defaultPlayout) ? status.defaultPlayout : "default";
    const url = `https://${status.publication.toLowerCase()}.bbvms.com/p/${playout}/c/${picked.id}.html`;

    editor.commands.setEmbeddedMedia({
      url,
      // The same inert text the oEmbed provider returns: no host, no script, nothing a visitor or a CSP can trip over.
      markup: `<span class="bb-oembed-ref">Blue Billywig video ${picked.id}. The player is added when the page is shown.</span>`,
      constrain: true,
      width: "500",
      height: "281",
    });
  }
}
