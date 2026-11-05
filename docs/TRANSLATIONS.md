# Translations

What is translated, where the translations came from, and which of them still need a native speaker. Parity rows: T1 (backoffice) and T2 (visitor texts) in [PARITY.md](PARITY.md). How it works in code: [ARCHITECTURE.md](ARCHITECTURE.md#translations). How to add a text: [DEVELOPMENT.md](DEVELOPMENT.md#adding-or-changing-a-translated-text).

## Locales

English (`en`, the source and the fallback) plus the reference plugin's seven: Danish (`da`), German (`de`), Finnish (`fi`), French (`fr`), Norwegian Bokmål (`nb`; `no` and `nn` read it for visitor texts), Dutch (`nl`) and Swedish (`sv`). The files are language-level (no `da-DK`): Umbraco and the server both fall back from a region to its language.

## Status in one paragraph

All eight files exist and are complete for the keys that exist; tests check that every file has exactly the English keys, no empty value and the same `{0}` placeholders. **No translation has been reviewed by a native speaker.** Apart from the 14 backoffice terms and the 6 visitor texts that were taken over as they are from the reference plugin, every text was written for this package (by the coding assistant, not by a translator) and **needs a native review before a customer sees it**. Neither the backoffice nor the visitor output was observed in a browser in another language.

## Visitor texts (T2): `src/BlueBillywig.Umbraco/Localization/Visitor/<lang>.json`

Rendered on the server from `CultureInfo.CurrentUICulture`; the two public scripts get theirs from a `data-bb-text` attribute on their script tag (`videoUnavailable` for `bb-lazy.js`, the `cta*` keys for `bb-cta-form-overlay.js`). Text typed by an editor is never translated.

| Key | English | Source of the other 7 |
|---|---|---|
| `consentMessage` | Watching this video will transmit data to Blue Billywig, our video provider. Clicking Load will grant consent for all Blue Billywig videos on this page. | Adapted from the reference plugin's sentence (it names the host with `%s`; this package's text names "Blue Billywig, our video provider") |
| `loadVideo` | Load video | Reused as is from the reference plugin's translations (exact English match, all 7 locales) |
| `loadChannel` | Load video channel | Reused as is from the reference plugin's translations (exact English match, all 7 locales) |
| `loadShorts` | Load Shorts experience | Reused as is from the reference plugin's translations (exact English match, all 7 locales) |
| `labelVideoChannel` | Video channel | Written for this package |
| `labelVideoPlayer` | Video player | Written for this package |
| `labelShorts` | Shorts | Written for this package |
| `labelTranscript` | Video transcript | Written for this package |
| `transcript` | Transcript | Written for this package |
| `transcriptLang` | Transcript ({0}) | Written for this package |
| `subtitles` | Subtitles | Written for this package |
| `chapters` | Chapters | Reused as is from the reference plugin's translations (exact English match, all 7 locales) |
| `videoChapters` | Video chapters | Reused as is from the reference plugin's translations (exact English match, all 7 locales) |
| `playVideo` | Play video | Written for this package |
| `playTitle` | Play: {0} | Written for this package |
| `watchChannel` | Watch our video channel | Written for this package |
| `videoUnavailable` | Video unavailable — try reloading the page. | Reused as is from the reference plugin's translations (exact English match, all 7 locales) |
| `gateRequiresAccess` | This content requires access | Written for this package |
| `gateNoPermission` | Your account does not have permission to view this content. | Written for this package |
| `gateLogInPrompt` | Please log in to watch this video. | Written for this package |
| `gateLogIn` | Log in | Written for this package |
| `ctaSubscribe` | Subscribe | Written for this package |
| `ctaEmail` | Email address | Written for this package |
| `ctaConsent` | I agree to be contacted by email. | Written for this package |
| `ctaPrivacy` | Privacy policy | Written for this package |
| `ctaSubmit` | Submit | Written for this package |
| `ctaThanks` | Thanks! | Written for this package |
| `ctaErrEmail` | Please enter a valid email address. | Written for this package |
| `ctaErrConsent` | Please accept the consent statement. | Written for this package |
| `ctaErrSubmit` | Could not submit — please try again. | Written for this package |
| `embedProblem` | Video problem: the player could not be prepared for this site's content security policy. See the log. | Written for this package (added with the embed problem marker; shown to signed-in backoffice users, or to everybody while `ShowEmbedProblems` is on) |

**Needs native review:** everything marked "Written for this package" or "Adapted" (25 of 31 keys, in all 7 languages = 175 texts). The reused six were translated by the reference plugin's translators. Points to check: the formal or informal address ("Sie" in German, "je" in Dutch), the wording of the consent sentence (it is a legal-ish notice; a site can replace it with its own text under **Settings > Blue Billywig > Consent message**, and sites with a consent platform will), the form overlay defaults (an editor's own labels win), and the gate message.

## Backoffice texts (T1): `src/BlueBillywig.Umbraco/Client/src/localization/<lang>.ts`

246 keys per language, area `bluebillywig`.

- **Reused from the reference plugin as is** (exact English match, 14 keys): `save`, `search`, `tabPlaylists`, `folders`, `allVideos`, `noVideos`, `typeChannel`, `playout`, `lazyLoad`, `height`, `publication`, `defaultsHeadline`, `customFields`, `connectionFailed` (French keeps its own "Publication", which the reference spells the same).
- **Everything else is written for this package and needs a native review**, in particular the long sentences (the settings confirm dialogs, the content protection banner, the call to action messages) and the error sentences that insert a noun (`loadFailed`, `noAccess`, `badRequest`, `saveGateway`, `saveNoPermission`, `saveFailed` with the `noun*` keys). Those were worded to read naturally with the noun on its own; check that they do, since the nouns cannot be inflected. The Finnish and French sentences put the noun in brackets for that reason; the Danish, Dutch, German, Norwegian and Swedish ones use the noun bare, in the indefinite plural.
- Terms that exist in Umbraco's own language packs (Save, Cancel, Delete) are deliberately in the package file too; if you prefer Umbraco's wording, change the package file, not the code.

### Translated

The embed editor additions (the "Load preview" button and its two notes, the draft, archived and embargo banner with **Publish now** and **Remove embargo**, **Live streams**, **Recent videos** and **Search results** in the library picker; keys `loadPreview`, `previewTitle`, `previewAuthenticated`, `previewPublic`, `nounPreview`, `nounClipStatus`, `bannerDraft`, `bannerArchived`, `bannerEmbargo`, `publishNow`, `removeEmbargo`, `clipPublished`, `embargoRemoved`, `liveStreams`, `recentVideos`, `searchResults`: 16 keys in 7 languages, all written for this package and **needing a native review**, in particular the banner sentences and `bannerEmbargo`'s "{0} UTC to {1} UTC"; the times are shown in UTC since 2026-11-05, all eight files updated), the credentials notice and gate banners (backoffice header and library views), the section tabs and the picker modal, the Upload view (the form, messages and limits text), the library browser (search, filters, bulk actions, the delete confirmation, empty and error states), the folder tree, the video picker (types, search, options), the settings dashboard (connection and defaults boxes, buttons, messages and confirm dialogs, the cleanup box, the labels of the content protection box), the call to action editor (mode tabs, type, title, form and link fields, timing, save and remove, status and error messages), and the error sentences of `library/errors.ts`.

### Not translated yet (these still show English whatever the language)

- The clip details modal, the clip edit modal (metadata, thumbnails, custom fields, subtitles, embargo, status), the clip tiles, per-clip analytics and the analytics dashboard, the Playlists table, the Platform view (headlines and links), the tag chips.
- Explanatory paragraphs and descriptions: the content protection box (intro and field descriptions), the Default embed type options, the call to action editor's canvas, styling, position, background image and form hints, and the dashboard and property editor names in the manifests (`Blue Billywig`, `Blue Billywig Video`, the rich text button tooltip).
- Server messages that the backoffice shows as they come: validation sentences from the API (`CtaValidator`, `GateSettingsStore`, the clip edit rules), and Uppy's own dashboard strings (Uppy has its own locale packs; adding them is a library decision to take first).
- Dates and numbers use the browser's locale and are not part of these files.
- The settings pages of other packages and Umbraco's own screens are Umbraco's.

When a view is moved, remove it from this list in the same change.

## Review checklist for a translator

1. Read the language's `en.ts` side by side with its file; the comments in the file mark the groups.
2. Placeholders `{0}`, `{1}` must stay; their order may change inside the sentence.
3. Keep product names (Blue Billywig, Shorts, OVP, Playout, CORS, API) as they are unless the language has an established term.
4. For the visitor files, also try the page: set the site culture or `Accept-Language` and look at the consent placeholder, a gate message, a transcript and a call to action form.
