Status: proposed, awaiting owner approval

# Authoring AI settings

Review record: [authoring-review.md](authoring-review.md)

Flow id: `authoring-ai-settings`. CR-D-2 phase: **G1**, Settings group, project scope.

Feature tasks: `13.4.1-T1.1`, `13.4.1-T1.2`, `13.4.1-T5.1`; role-profile editor delta `13.2.2-T6.2`.

Legend: ✅ source-verified with a link; ⚠️ unverified; `[INFERENCE]` recommended design interpretation; `[DECISION]` an existing source decision, not approval here; `[OPEN]` unresolved with a recommended default. This specification proposes the supplied visual structure; it does not certify a browser, accessibility, API integration or owner-review pass. Design review and owner approval are separate gates in [feature §10](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#10-tasks-sub-tasks-and-re-estimate).

Data traces below use `contracts/openapi.yaml` as `O`. `AiSettingsDocument` resolves through its `allOf` to [ai-settings.v1.json](../../../contracts/schemas/ai-settings.v1.json). A dotted member after a schema is the displayed field, not a new schema name. Every uncontracted field is explicitly open; illustrative `ais-3` is not a wire identifier.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Project name / slug / scope id | `contracts/openapi.yaml#/components/schemas/ProjectDto.name`, `.slug`, `.id` | traced; use already-authorized shell context, not an extra project-read prerequisite |
| Current version, settings identity and origin | `contracts/openapi.yaml#/components/schemas/AiSettingsView.version_no`, `.settings_id`, `.source` | traced; label vN, not fabricated `ais-N`; default is version 0 with null identity |
| Content hash / copied-template code and version | `contracts/openapi.yaml#/components/schemas/AiSettingsView.content_hash`, `.template` → `AiSettingsTemplateRef.code`, `.version` | traced; full safe identity inspectable |
| Description | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument.description` via external schema `#/properties/description` | traced; shipped maximum 1,000 code points |
| Environment URL | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument.environment_url` via external schema `#/properties/environment_url` | traced; nullable, maximum 2,048; no credentials |
| Output languages | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument.languages` via external schema `#/properties/languages` | traced; one or two unique `en` / `vi`, independent of interface locale |
| Writing style / coverage strategy | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument.writing_style`, `.coverage_strategy` | traced; maxima 250 / 1,500 |
| Critical / high / medium / low priority criteria | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument.priority_criteria` → `AiSettingsPriorityCriteria` → external `#/properties/priority_criteria/properties/{critical,high,medium,low}` | traced; four separate strings, each maximum 250 |
| Exclusion rules / step detail | `contracts/openapi.yaml#/components/schemas/AiSettingsDocument.exclusion_rules`, `.step_detail` | traced; maxima 1,500 / 200 |
| Head author membership, reason, UTC time | `contracts/openapi.yaml#/components/schemas/AiSettingsView.author`, `.reason`, `.created_at` | traced; null means not supplied, never the client time |
| Save reason | `contracts/openapi.yaml#/components/schemas/SaveAiSettingsRequest.reason` | traced; optional, 1–500 when supplied |
| Version-list number/hash/template/author/reason/time | `contracts/openapi.yaml#/components/schemas/AiSettingsVersionSummary.version_no`, `.content_hash`, `.template`, `.author_membership_id`, `.reason`, `.created_at` | traced; fetch each selected document through `AiSettingsVersion.settings` before comparing |
| History cursor / exact immutable version identity | `contracts/openapi.yaml#/components/schemas/AiSettingsVersionListResponse.next_cursor`; `contracts/openapi.yaml#/components/schemas/AiSettingsVersion.settings_id`, `.version_no`, `.settings` | traced; current draft and historical document remain separate |
| Template label / language / default / code / version / hash / guidance | `contracts/openapi.yaml#/components/schemas/AiSettingsTemplateView.name`, `.locale`, `.is_default`, `.code`, `.version`, `.content_hash`, `.settings` | traced; catalogue is read-only, `AiSettingsTemplateListResponse.next_cursor` is null |
| Field error, current-version pointer, request id, retryability | `contracts/openapi.yaml#/components/schemas/ErrorEnvelope.error.details[].path`, `.reason`, `.limit`, `.actual`; `.error.code`, `.error.request_id`, `.error.retryable` | traced; pointers are not server field snapshots |
| Live count and before/after field differences | Input members above; two fetched `AiSettingsVersion.settings` documents | traced inputs; `[INFERENCE]` local presentation only, not server diff/count fields |
| Human author name such as Linh Nguyễn | No display-name member in settings responses | [OPEN] A1: show safe membership identifier until an authorized member-name mapping is supplied; never resolve through a stronger permission |
| Tenant opt-in on/off/unavailable, policy source and administration destination | No member in `AiSettingsView` or template/history reads | [OPEN] A2: show unavailable with an explanation when no authoritative status is supplied; do not perform a generation estimate merely to decorate this page or invent an admin URL |
| Role-profile name, description, language, style/priority instructions, allowed output kinds | No role-profile component or operation in current O | [OPEN] A3: use the planned closed content from [role-profile §3](../../../plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md#3-contracts-touched); name 80, description 300, combined `style_instructions` 1,500 code points; language vi/en; G1 kinds checklist/test_case, pending contract delivery |
| Role identity/version/hash, retired flag, author/reason/UTC time, history cursor | No role-profile component in current O | [OPEN] A3: require the planned exact-version read and receipt; no locally invented version, retirement or success |
| Seed id/code/version/kind/locale, defaults and provenance; clone reference | No role-prompt-template component in current O | [OPEN] A3: read-only platform seeds, explicit clone to custom v1 only; never edit a seed body |
| Custom-profile quota/current count/limit; field or quota refusal | No role-profile limit/read/error shape in current O | [OPEN] A4: recommended limit 20 retained identities, including retired; only display authoritative usage, otherwise unavailable, never inferred remaining capacity |

## v2 pages

| Page | Role | Phase | Valid `?state=` values |
| --- | --- | --- | --- |
| [44 — AI settings](../web-ui/output/44-ai-settings/evidgate-ai-settings.html) | Primary guidance form, template preview and immutable history | G1 | `default`, `loading`, `empty`, `error`, `403`, `404`, `401`, `412`, `429`, `submitting`, `saved`, `validation-error` (HTML:77) |
| [27 — Settings shell](../web-ui/output/27-settings-shell/evidgate-settings.html) | Entry, selected project, read-only tenant consent notice; not a second editor | G1 entry | See [settings.md](settings.md#states); B1-05 supplies the delta |
| Role-profile editor next to AI settings | Proposed sibling editor surface within the same project Settings context; the generation selector stays inside the generation flow design | G1 | [OPEN] No role-profile editor or query-state fixture is present in page 44 |

✅ [B2-44](../web-ui/prompts-crd2/B2-44-ai-settings.md), [B1-05](../web-ui/prompts-crd2/B1-05-settings-ai.md), [delta brief](../web-ui/prompts-crd2/00-delta-brief.md#authoring-vocabulary-plan-13-epics-131134) and [screen map](../web-ui/prompts-crd2/00-README.md#1-review-what-each-existing-page-needs) define the visual input. Query states are local review specimens, not HTTP responses. No production phase/state switch is proposed.

## Regions

Reading and focus order preserves page 44's stack, not a new dashboard:

1. Skip link → product navigation → scope/topbar → breadcrumbs → narrow-view notice → page h1, current version and supplied author/time; return to Settings (HTML:12–76).
2. Request-state region before normal content (HTML:77–135). Production excludes review controls. A load failure is not a default-template result.
3. Read-only tenant opt-in notice, with real status/source only when supplied. Off explains that manual authoring and project-settings saves remain possible; consent is not editable here (HTML:137–144).
4. Guidance section with Settings / Version history tabs (HTML:146–164). Settings: template-preview action → description → environment URL → output languages → writing style → coverage strategy → four priority criteria → exclusion rules → step detail → optional reason → single primary Save new version (HTML:165–240). The counter specimen toolbar is review-only.
5. Version history: newest-first keyset summaries → choose two fetched immutable documents → labelled before/after text differences, never editable history (HTML:243–255). Empty history does not remove effective default settings.
6. Template dialog: explicit vi/en catalogue choice and field preview → Cancel → Replace draft. Replacement changes only the draft after confirmation; Cancel/Escape retains every draft value (HTML:281–312). Interface language does not silently choose the template/output language.
7. Role-profile editor delta: a sibling destination adjacent to guidance, preserving project context and back navigation. Order: custom-profile list and read-only seed catalogue → explicit New/Clone → name, description, language, allowed G1 kinds, one combined style/priority instruction field → optional reason → Save new version; separate read-only exact-version history and explicit retirement/reactivation confirmation. No base prompt, model/provider/schema, permission editor, per-run note or consent switch. The optional single-run note belongs only in generation.

Reuse A15 catalogue: `app-page-header`, `app-section-header`, `app-request-state`, `app-inline-feedback`, `app-conflict-dialog`, `app-retry-after`, `app-revision-chip`, `app-hash-chip`, `app-copyable-identifier`, `app-number-value`, `app-absolute-time`, `[appDesktopMutation]`, `app-desktop-required-notice`, `app-field-feedback`, `app-form-error-summary`; `app-data-table` family where needed. Owned Helm fields/tabs/dialogs/buttons remain the primitive layer; use line tabs per UI9. NEW names only: `shared/ui/authoring/character-counter`, `shared/ui/authoring/version-diff`, `shared/ui/authoring/template-preview`, `shared/ui/authoring/role-profile-summary`. These are presentational proposals, not claims of existing components; no feature data access belongs in them. The component/id map is reserved for the handover document.

## States

The following expands every §6 AI-settings requirement into **22 concrete cases across 14 rows** from [feature §6](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#6-states-and-transitions). Below/at/above are separate boundary cases, not a generic validation banner.

| State | Region behaviour / transition | Mockup `?state=` or gap |
| --- | --- | --- |
| No settings yet | Effective default document, `version_no=0`, `settings_id=null`, `ETag: "0"`; first save uses that tag; history empty, no fake saved row | [OPEN] A5: `empty` is a generic empty panel, not the contracted default-document state |
| Dirty draft | Keep base tag and every field, including output languages/reason; explicit save only | `default` after local edit; no dedicated dirty query |
| Writing style below / at / above 250 | 249/250 valid; 251 marked invalid, counter/error associated, save blocked without truncation | `default` local fill controls 249/250/251 (HTML:222); `validation-error` banner |
| Coverage strategy below / at / above 1,500 | 1,499/1,500 valid; 1,501 blocks save and identifies field | `default` local fill controls (HTML:223); `validation-error` |
| Exclusion rules below / at / above 1,500 | 1,499/1,500 valid; 1,501 blocks save and retains full text | `default` local fill controls (HTML:224); `validation-error` |
| Step detail below / at / above 200 | 199/200 valid; 201 blocks save, no silent shortening | `default` local fill controls (HTML:225); `validation-error` |
| Template preview | Fetch catalogue; select explicit vi/en template; compare all changed draft members and show provenance, no write | `default`, open `#template-dialog`; [OPEN] A6: fixture previews both languages but has no explicit template choice |
| Cancelled replacement | Cancel/Escape leaves draft, dirty flag, base tag and current history untouched; restore opener focus | `default`, cancel template dialog |
| Save loading | Local form busy, duplicate submission blocked, draft still visible; one intent/key | `submitting` |
| Save error | Retain draft; 422 field/secret/url errors next to fields plus linked summary; 409 intent mismatch explained; 413 body too large not a saved version | `error` is generic load failure; [OPEN] A7: distinct retained-draft save-failure fixture needed |
| Save success | 201: show returned vN/time and append history after confirmed read; 200 canonical no-op: “No changes”, existing version, no invented append | `saved` is explicitly local only; [OPEN] A7: no-op specimen absent |
| 412 | Keep every unsaved field; show base/current version and fetched comparison if available; shared conflict dialog, explicit Reload only, no merge/retry | `412`; [OPEN] A8: fixture names only two draft fields and static current values, not a full server comparison |
| Read-only version | View exact fetched immutable vN and compare; no overwrite or save action on that historical document | `default` → Version history tab |
| Opt-in off | Read-only off notice; generation blocked elsewhere, manual authoring and settings save unaffected | `default` contains off notice; on/unavailable lack dedicated query specimens |

Shared request states are additional: `idle → loading → ready|error`; initial loading marks only its region busy. On refresh failure retain readable data with stale context, not a blank page. 401 preserves unsent draft without automatic submission after sign-in; 403 read denial hides all settings data, whereas readable-but-not-writable keeps the form readable and Save explained-disabled; foreign/unknown project/version uses the same 404. 429/503 honour server `Retry-After` via the shared wait pattern, no fabricated countdown or retry loop. An offline-ish error retains the in-memory draft, not a promised persisted offline cache. Mockup `loading/error/401/403/404/429` cover shared specimens; [OPEN] A7 also covers stale/503/read-only-grant fixtures.

Role-profile delta states (all `[OPEN] A3`, absent from mockup 44): loading; empty custom list with selectable seeds; load error; 403 read denial; readable/selectable but create/clone/save disabled without `project:write`; dirty draft; name/description/instructions at N−1/N/N+1 and invalid/secret text; create/clone success v1; changed save success vN+1; no-op 200; retained-draft save failure; 412 with explicit Reload; quota refusal; immutable historical version; retire/reactivate confirmation and success; selected profile retired/missing/wrong-kind after a refresh. Last case invalidates generation selection with a reason, never silently substitutes another profile. Planned role caps and grant mapping remain `[INFERENCE]` pending the role plan's open decisions, not an RBAC role inference.

Accessibility evidence is source-based, not exercised: page 44 has labelled inputs, a polite form-status region (HTML:185), tab relationships (160–164), and explicit template cancellation. Required implementation: native focus order as Regions; Arrow/Home/End within tabs; one modal trap with localized Close, Cancel initially focused for replacement, Escape and return focus. Associate counters/hints/errors using `aria-describedby`; only announce limit-crossing or submit outcome, not every keystroke. Failed submit focuses the linked summary; summary links focus fields. Conflict comparison fetch failure says unavailable and leaves draft intact. Background history/status refresh never steals focus.

**Reference deltas AR-05/06:** count the NFC-normalized, end-trimmed projection of each draft as Unicode code points, not JavaScript UTF-16 `value.length` and not graphemes. Do not impose native `maxlength` at the contractual cap: it can reject supplementary characters early. Preserve the full editable draft on typing/paste/IME composition, including over-limit input; validate committed composition, blur/correction and submit without truncation. The visual counter is a non-live `aria-describedby` description, outside any `role="status"`/live ancestor. Use one separate polite status region only when entering an at-limit/over-limit state, returning within the limit, or reporting submit outcome; further keystrokes within the same state and background refresh do not announce. A focused submit-error summary must not be duplicated by another live reading of the same error.

Boundary evidence must include normalized N−1/N/N+1 values for every shipped cap, supplementary characters (one code point each), decomposed Vietnamese accents (NFC before counting), paste, IME and correction. The frozen reference's UTF-16 counter/native truncation and per-keystroke `aria-live="polite"` are explicitly not approved boundary or AT evidence; runtime/AT checks remain open in [authoring-review.md](authoring-review.md).

## Copy length (vi vs en)

Longest supplied labels and proposed safety messages below are stress specimens, not measured rendered widths or a computed global maximum. Allow 40% expansion, natural Vietnamese wrapping and full recoverable user text; no fixed-height prose, native-title-only recovery or locale-dependent content mutation.

| Purpose | vi | en |
| --- | --- | --- |
| Title / primary | Cài đặt AI của dự án / Lưu phiên bản mới | Project AI settings / Save new version |
| Long field label | Phiên bản cài đặt đã dùng | Settings version used |
| Limit error | Vượt giới hạn ký tự. Giảm trường được đánh dấu trước khi lưu. | Character limit exceeded. Reduce the marked field before saving. |
| 412 | Bản nháp được giữ nguyên. Tải lại phiên bản hiện tại khi bạn sẵn sàng; không tự hợp nhất hay gửi lại. | Your draft is unchanged. Reload the current version when ready; nothing is merged or resubmitted automatically. |
| Opt-in off | Chưa bật tạo bằng AI; vẫn có thể soạn kiểm thử thủ công. Lưu cài đặt dự án không bật chấp thuận của tổ chức. | AI generation is off; manual authoring is available. Saving project settings does not enable organization consent. |
| Long supplied exclusion preview | Loại trừ các thao tác phá huỷ trên môi trường sản xuất và mọi bước cần thông tin thanh toán thật. | Exclude destructive production actions and any step requiring real payment credentials. |
| Opt-in unavailable | Không có dữ liệu trạng thái chấp thuận AI. Không suy ra rằng tổ chức đã chấp thuận. | AI consent status is unavailable. Organization approval is not assumed. |
| No-op | Không có thay đổi; phiên bản hiện tại được giữ nguyên. | No changes; the current version is unchanged. |
| Profile quota / secret refusal | Đã đạt giới hạn hồ sơ. / Nội dung có dấu hiệu chứa bí mật; không có gì được lưu. | Profile limit reached. / The text appears to contain a secret; nothing was saved. |
| Invalidated selection | Hồ sơ đã chọn không còn khả dụng cho loại kết quả này. Hãy chọn lại rõ ràng. | The selected profile is no longer available for this output kind. Choose another explicitly. |

Use compact status labels with the complete reason outside the chip per UI14–UI19. Raw machine identifiers/reason codes remain inspectable with localized explanation; unknown reason gets “Không thể hoàn tất thao tác này / This action could not be completed”, not a success fallback. Never echo rejected secret text.

## Responsive notes

- **1280 px:** expanded 224px sidebar; authored vertical content order and bounded guidance form, related field pairs side by side where space permits; history/diffs remain below their tabs. Use existing content cap, 24px section rhythm and shared token typography, not a new font/palette. Tables scroll only in their named local region.
- **360 px:** modal navigation drawer and 12px gutters; tab choice/read-only sections stack. Show the desktop-required notice, every saved guidance value, output language and version-history fact. No editing, template replacement, clone, retire/reactivate, save or import/apply action. Read-only is not a global disabled page: navigation, history and full-value inspection still work. Unsaved desktop draft is retained through resize and returns when wide again; read-only projection must not claim the draft is saved.
- Replace mobile hidden controls through the shared narrow policy, not CSS-only focus loss. If a focused editor control disappears on resize, hand focus to the corresponding read-only region/notice after render. Drawer traps focus and returns to toggle on Escape; no page-wide overflow clipping.
- Long URLs and profile names wrap safely; full safe URLs remain text, never credential-revealing links. Compare panes stack before they squeeze; no essential errors/reasons truncated. Existing `--eg-*` theme, control-border and focus recipes from [tokens](tokens.md#rules-for-components) and [state sheet](state-sheet.md#shared-accessibility-and-themes) apply unchanged.
- ⚠️ Manual acceptance still required for this screen and role delta at **1280/360 × light/dark × vi/en**: WCAG 2.5.8 targets ≥24px (44px touch), 2.4.11 focus unobscured by sticky header/dialog/toast, 2.5.7 no drag-only operation, 1.3.1 form/table relationships, 1.4.3 text ≥4.5:1 and 1.4.11 necessary controls/focus ≥3:1, 1.4.10 reflow, 2.1.1 keyboard, 2.4.3/2.4.7 focus order/visibility, 3.3.1/3.3.2 errors/labels and 4.1.3 status announcements. No visual or screen-reader pass is claimed by this document.

## Invariants

- ✅ [O settings operations](../../../contracts/openapi.yaml) require `authoring:read` for all four reads and `project:write` for Save. **OI-13-509 remains open:** registry entry currently uses `project:read` (nav-registry.ts:310–321), so its visibility disagrees with page authorization. Recommended gate is `authoring:read` for entry and direct-route read; Save additionally requires `project:write`. Phase filtering occurs first. No extra permission or inferred role mapping.
- Save `If-Match` is the effective head's quoted `version_no`, including `"0"`, plus one `Idempotency-Key` per user intent; retry of that identical intent reuses it. Missing tag is server 422, not client 428; stale tag wins before no-op and changes nothing. Conflict details are pointers: fetch current authorized content separately; never treat `reason` as a snapshot.
- 201 appends one immutable settings version; canonical equal head returns 200 with no append/audit. Reverting to older content is a new version. History is read-only; diff requires both exact documents and never rewrites generated provenance/cache references.
- Normalize NFC and end-trim a validation/submission projection before counting Unicode code points consistently with the server; an astral emoji is one code point, while a multi-code-point grapheme is not automatically one. Keep the original in-memory draft editable; never use UTF-16 `maxlength`/`value.length` to enforce these caps or silently truncate over-limit content. Reject invalid/secret text with associated field feedback. Contract ships 1,000 description, 2,048 URL and 250 per criterion in addition to the four product caps; the older brief's uncapped treatment is not current wire truth.
- Template is provenance, not an equality requirement. Preview/replacement touches draft only. Interface locale, template locale and generated output languages are distinct.
- Null is not zero: unknown author/time/consent/quota is unavailable/not set as appropriate; v0 is a real default-state value, not a fabricated saved row. No cost, percentage, ETA or inferred progress appears on this screen.
- All guidance/profile/diff/error text is untrusted plain text; never `innerHTML`, credential-bearing URL display, base-prompt editing, provider/model controls, consent mutation or permission-from-profile-name. Role saves use their own quoted N≥1 tag, never settings' v0; exact pins remain pinned after later saves. Role create/clone/save receipts require an authoritative subsequent read before showing full content.

## Open points

| Item | Gap / recommended default |
| --- | --- |
| OI-13-509 | Re-gate AI-settings entry to `authoring:read`, with readable/no-write Save explained-disabled and tests for both grant combinations; do not fix registry in this docs-only assignment. |
| A1 — names | Settings returns membership ids, not display names. Prefer id until a permitted mapping exists; never force project/member permission just to decorate history. |
| A2 — consent | Page 44 hardcodes off; on/unavailable/policy-source contracts and exact admin destination are absent here. Prefer unavailable with explanation and known Settings return link only; never imply this link toggles consent. |
| A3 — role-profile contract and editor | Role plan specifies six planned operations and editor states, but none are in current OpenAPI and page 44 has no editor. Proposed sibling layout/fields/states above are design-only. Contract owner must deliver generated schemas/operations before UI build; no hand-written DTO, invented URL or fake success. |
| A4 — role defaults | Role plan OI-13-301/302 keeps save mapping and text/quota numbers recommended. Use existing `project:write` default, no role-name inference; confirm finite limits and returned metadata together before writes. |
| A5 — defaults | Replace generic empty-page interpretation with effective default guidance and v0; history alone is empty. Distinct fixture needed. |
| A6 — templates and counters | Explicit vi/en template selection and full-member replacement preview are not shown by the static dialog; add the design specimen without copying its four-field-only assumption. Description/URL/criteria counters must reflect shipped caps; retain N−1/N/N+1 specimens without silent truncation. |
| A7 — missing state specimens | Add retained-draft save error, canonical no-op, stale reader, 503 wait and readable-without-write specimens; never use load-error/403 as substitutes. |
| A8 — conflict completeness | Static 412 shows two draft fields and fixture current values. Shared dialog must preserve every field and fetch authorized current content; unavailable comparison remains explicit. Localized Close/mobile select labels also need real vi/en evidence. |
| Review gate | Independent review observations and docs-only correction dispositions are recorded in [authoring-review.md](authoring-review.md). Reference deltas, complete themed/bilingual/Unicode/AT evidence and owner approval remain open; keep proposed status and do not begin UI implementation until the separate gates are recorded. |
