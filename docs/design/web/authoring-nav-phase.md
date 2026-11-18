Status: proposed, awaiting owner approval

# Authoring navigation by phase

Review record: [authoring-review.md](authoring-review.md)

Flow id: `authoring-nav-phase`. CR-D-2 phases: **G1–G4** entry filtering; the shell itself is **G1**.

Feature tasks: `13.4.1-T1.1`, `13.4.1-T1.2`, `13.4.1-T7.1`; registry owner `09.1.1-T4`; contract follow-ups `OI-13-509` (AI-settings gate) and `OI-13-504` (credit read).

Legend: ✅ source-verified with a link · ⚠️ not exercised · `[INFERENCE]` interpretation · `[DECISION]` recorded decision · `[OPEN]` unresolved, with recommended default.

✅ Sources: [authoring plan §2 AC14, §6 and T1/T7](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#10-tasks-sub-tasks-and-re-estimate), [B1-01](../web-ui/prompts-crd2/B1-01-nav-shell.md), [CR-D-2 delta brief](../web-ui/prompts-crd2/00-delta-brief.md#navigation-rule), [00 README page table](../web-ui/prompts-crd2/00-README.md#1-review-what-each-existing-page-needs), the live [nav registry](../../../apps/web/src/app/shell/nav/nav-registry.ts#L47), its [models](../../../apps/web/src/app/shell/nav/nav.models.ts#L6), [layout](layout.md#breakpoints-and-navigation), [tokens](tokens.md#control-sizes-and-layout-dimensions), [state sheet](state-sheet.md), and the [A15 catalogue](../../engineering/ui-rules.md#shared-component-catalogue-a15).

This document specifies the shell regroup and the five CR-D-2 page deltas. It is proposed design only: no independent design review, browser/AT run, gate or owner approval is claimed. The mockup's phase select is a review affordance and never a production control.

## v2 pages

| Page | Role in this design | Phase | Valid `?state=` values |
| --- | --- | --- | --- |
| [01 · Workspace shell](../web-ui/output/01-app-shell/evidgate-workspace-shell.html) | **Primary**: global sidebar rendered from the shared nav array, topbar, authoring workspace specimen | G1 | `default`, `loading`, `empty`, `no-match`, `error`, `403`, `412` (HTML:94) |
| [23 · Onboarding wizard](../web-ui/output/23-onboarding-wizard/evidgate-onboarding.html) | Delta: G1 path; CI continuation from G2 | G1/G2 | shared switcher plus B1-03 states |
| [24 · Project overview](../web-ui/output/24-project-connection/evidgate-project-overview.html) | Delta: authoring home; G2 CI regions hidden in G1 | G1 | shared switcher plus B1-02 states |
| [27 · Settings shell](../web-ui/output/27-settings-shell/evidgate-settings.html) | Delta: AI-settings destination and tenant opt-in status; G4 tabs phase-filtered | G1 | shared switcher plus B1-05 states |
| [31 · Usage and retention](../web-ui/output/31-quota-retention/evidgate-usage-retention.html) | Delta: G1 credit region; resource quota/retention stay G2. Detailed in [authoring-usage-credits.md](authoring-usage-credits.md) | G1/G2 | `default`, `loading`, `empty`, `no-match`, `table-error`, `error`, `403`, `404`, `401`, `429`, `412` (31 HTML:78) |

The production shell has **no phase switch** (AC14). B1-01's labelled `Giai đoạn` / `Phase` select exists only inside `.review-dock[data-od-id="design-review-controls"]` of the static pages, is excluded from production bundles and never grants permissions or upgrades a workspace.

The sidebar is one registry, not per-page markup: group order, entry order, phase, scope, segment and permission all come from `NAV_GROUPS` and `NAV_ENTRIES`. Current source holds **30 entries in 5 groups** (5 design, 4 runs, 4 learn, 6 release, 11 settings) — count verified in the registry file, not copied from an older note.

Only wire-backed fields are cited through `contracts/openapi.yaml`; a navigation field that exists only in the registry or router says so in the path cell and is never presented as an API contract. Anything a delta screen shows without a contract field is `[OPEN]` with a recommended default.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Group id and localized label (`design`, `runs`, `learn`, `release`, `settings`) | `apps/web/src/app/shell/nav/nav-registry.ts` `NAV_GROUPS`; ids/`@@shell.nav.group.*` cited by `plans/features/13-product-phases/13.4.1-authoring-ui.md` AC14 | traced in code; vi/en strings live in `apps/web/src/locale/messages.vi.xlf` |
| Entry id, icon, order, scope, segment, area | `apps/web/src/app/shell/nav/nav.models.ts` `NavEntry`; `nav-registry.ts` `NAV_ENTRIES` | traced in code; not an API payload |
| Entry phase | `apps/web/src/app/shell/nav/nav.models.ts` `NavEntry.phase`; filtered by `apps/web/src/app/shell/nav/product-phase.ts` | traced in code; the active value is build configuration |
| Entry permission | `apps/web/src/app/shell/nav/nav.models.ts` `NavEntry.permission`; codes from `contracts/permissions.json`; catalogue route `contracts/openapi.yaml` `/permissions` | traced (code + permission catalogue); no per-entry API field |
| Current location / active entry | Angular router URL, not a contract field | traced at design level; no API field |
| Organization and project scope labels | `contracts/openapi.yaml#/components/schemas/OrganizationDto.name`; `contracts/openapi.yaml#/components/schemas/ProjectDto.name`; `contracts/openapi.yaml#/components/schemas/ProjectDto.org_id` | traced |
| Account/membership display identity | `contracts/openapi.yaml#/components/schemas/MembershipView` (membership list); session identity shape not read in this slice | [OPEN] trace the shell identity read before implementing the account menu; no invented display name |
| Nav search results and hidden-entry matching | No contract field; registry filter | [OPEN] recommend search over visible, permitted entries only, with the same phase/permission predicate as the sidebar |
| Authoring-home counts, coverage and queue summary (page 24) | `contracts/openapi.yaml#/components/schemas/AuthoringTreeResponse`; per-case coverage: `ManualCaseListItemDto.linked_live_count`, `.linked_case_ids`; no `pending_change_count` projection | traced for per-case coverage via the authorized manual-case list; [OPEN] aggregate home counts need their owning scoped read and must not be summed from a partial page. `OI-13-503` concerns **pending-change counts only** (absent renders “—”, not `0`) |
| Recent versions (page 24) | `contracts/openapi.yaml#/components/schemas/AuthoringVersionListItemDto.version_no`, `.source`, `.author`, `.reason`, `.created_at` | traced |
| AI credit summary (page 24) | No schema; `OI-13-504` | [OPEN] recommend no numeric balance duplicated on the home; link to the org usage page |
| Onboarding step identity/completion (page 23) | No contract field for a step count or completion flags; resources are read individually | [OPEN] see the UI62 conflict below; do not invent a step DTO |
| Settings version and tenant opt-in (page 27) | `contracts/openapi.yaml#/components/schemas/AiSettingsView.version_no`; opt-in has no schema in this contract | traced for version; [OPEN] for opt-in source (plan fixes it to the generation estimate) |
| AI-settings destination guard (page 27, registry) | Registry `ai-settings` permission `project:read` vs 13.2.1 routes requiring `authoring:read` | [OPEN] `OI-13-509`: re-gate on `authoring:read` in T5.1 |
| Usage/credit entry and page (page 31) | Registry `quota` row (`usage:read`, org scope, segment `usage`) | traced in code; credit payload [OPEN] `OI-13-504` |

## Regions

Reading order of page 01 shell ([HTML:12–94](../web-ui/output/01-app-shell/evidgate-workspace-shell.html)):

1. Skip link and shell frame; navigation aside precedes the main column, topbar stays inside the main column ([layout.md](layout.md#page-grid-and-spacing)).
2. Sidebar: optional search, the five group labels in journey order, then entries; expanded at ≥1280 px, 68 px icon rail 768–1279 px, modal drawer below 768 px. Rail keeps accessible names; it does not remove labels from the accessibility tree.
3. Topbar: organization/project scope, breadcrumbs, account menu. Scope is read from the current route and the authorized identity, never from a sidebar group.
4. One page `h1`, description and primary authoring action; a loading/empty/error/forbidden slot before normal content.
5. Authoring workspace specimen and secondary links. At G1–G3 no release verdict, waiver, CI-health panel or policy link is present — not even as a review specimen; G4 specimens stay G4-only.

Group and entry map (authoritative for this design; phase, permission and scope as written in the registry):

| Group | Entry id | Phase | Permission | Scope | Segment |
| --- | --- | --- | --- | --- | --- |
| Test design | `authoring` | G1 | `authoring:read` | project | `authoring` |
| Test design | `generation` | G1 | `authoring:generate` | project | `generation` |
| Test design | `pending-changes` | G1 | `authoring:write` | project | `pending-changes` |
| Test design | `overview` | G1 | `project:read` | project | `overview` |
| Test design | `onboarding` | G1 | `project:create` | org | `onboarding` |
| Scripts & runs | `scripts` | G2 | `run:read` | project | `scripts` |
| Scripts & runs | `catalog` | G2 | `case:read` | project | `catalog` |
| Scripts & runs | `mapping` | G2 | `case:read` | project | `mapping` |
| Scripts & runs | `runs` | G2 | `run:read` | project | `runs` |
| Learn & repair | `failures` | G3 | `healing:triage` | project | `failures` |
| Learn & repair | `repairs` | G3 | `healing:triage` | project | `repairs` |
| Learn & repair | `knowledge` | G3 | `healing:triage` | project | `knowledge` |
| Learn & repair | `availability` | G3 | `healing:triage` | project | `availability` |
| Release | `studio` | G4 | `draft:write` | project | `studio` |
| Release | `suites` | G4 | `suite:read` | project | `suites` |
| Release | `quality` | G4 | `run:read` | project | `quality` |
| Release | `releases` | G4 | `run:read` | project | `releases` |
| Release | `policies` | G4 | `policy:read` | project | `policies` |
| Release | `deploy` | G4 | `environment:write` | project | `deploy` |
| Settings | `settings` | G1 | `organization:read` | org | `settings` |
| Settings | `ai-settings` | G1 | `project:read` [OPEN `OI-13-509`] | project | `ai-settings` |
| Settings | `members` | G1 | `membership:read` | org | `members` |
| Settings | `quota` | G1 | `usage:read` | org | `usage` |
| Settings | `audit` | G1 | `audit:read` | org | `audit` |
| Settings | `secrets` | G2 | `ingest_token:read` | project | `secrets` |
| Settings | `security` | G2 | `security_event:read` | org | `security` |
| Settings | `support` | G2 | `support:read` | org | `support` |
| Settings | `agents` | G4 | `agent_pool:read` | org | `agents` |
| Settings | `integrations` | G4 | `webhook:read` | project | `integrations` |
| Settings | `tokens` | G4 | `service_identity:read` | org | `tokens` |

Phase isolation must be enforced twice: the sidebar predicate hides later-phase entries, and the route guard/canMatch leaves their routes **unmatched** (a typed deep link does not render the page). Hiding is not authorization: the permission predicate is separate and runs after phase filtering. Hiding an entry must never fabricate an active item, throw, or reveal a filtered destination.

Reused A15 components: `app-page-header` with `[appPageHeaderActions]`, `app-section-header` with `[appSectionHeaderActions]`, `app-request-state`, `app-inline-feedback`, `app-forbidden-pattern`, `app-not-found-pattern`, `app-absolute-time`, `app-number-value`, `app-data-table` where lists exceed 200 rows, `app-desktop-required-notice`; Helm `dropdown-menu` for the account menu and owned Helm buttons/select for scope and review controls. NEW names only: `shared/ui/authoring/nav-phase-group` (presentational group heading + hidden-count-free list) and `shared/ui/authoring/credit-summary` (shared with the usage page). The later handover file owns the component/id map; no feature-local copy of a sidebar or state panel is allowed.

### Delta 01 — app shell and navigation (primary)

- Registry gains a `phase` per entry (already present in source) and rendering filters by the build's `ACTIVE_PHASE` **before** permissions; empty groups disappear.
- Group order is Test design → Scripts & runs → Learn & repair → Release → Settings; `overview` and `onboarding` sit inside the first group, not in a preceding Overview group.
- G1 opens authoring, generation and pending changes first. The test-case editor and import/export use `data-nav="authoring"`; they never add duplicate sidebar rows.
- Settings: `ai-settings` is a G1 destination; `secrets`, `security`, `support` start at G2; `agents`, `integrations`, `tokens` at G4. Settings, members and usage destinations remain visible only when both phase and permission admit them.
- Review-only phase select stays in the static review dock, default G1, and must preserve focus and never leave an invisible entry focused or active. No production phase switch.
- Page 01's run-heading/status-line/evaluation specimen is replaced by the authoring workspace specimen; G1–G3 carry no release verdict or waiver specimen.

### Delta 23 — onboarding wizard

B1-03 asks for four G1 steps (project → AI settings → first requirement → first checklist) and a G2 CI continuation, with the estimate/balance shown before checklist generation.

**Blocking conflict, not silently resolved:** the binding [UI62](../../engineering/ui-rules.md#theme-i18n-and-accessibility) requires onboarding to keep **all five current steps in order** with one top step navigator until an owner-approved amendment changes it. Recommended default: ship the five shipped steps, mapping the four B1-03 G1 milestones onto them and moving CI/run continuation to G2, and record the renumbering as an owner decision before any four-step implementation. Do not fork a second wizard.

The delta also removes CI as a G1 prerequisite, keeps entered content on failure, and forbids claiming a generated checklist while AI opt-in is off. The credit figure before generation is the estimate's own `credit_cost_micro`/`credits_available_micro` ([authoring-usage-credits.md](authoring-usage-credits.md)), not the usage-page fixture.

### Delta 24 — project overview becomes the authoring home

Adopt: authoring counts and coverage, recent immutable versions, pending-changes summary, one primary “Create requirement” action, onboarding progress/resume, and an authoring-oriented no-projects state. CI health, latest-run and policy links are absent in G1–G3, with no empty placeholder.

Two proposed restraints: (a) the visible credit card duplicates the single authoritative balance that AC14/T7.2 places on the org usage page, so the home should carry a clearly scoped link (or an unavailable/link state) rather than a second numeric balance; (b) counts must come from the reads that own them, and an absent count renders “—”, never `0`; `OI-13-503` covers only the missing pending-change projection, not the already-supplied per-case coverage fields. Both home restraints are recorded `[OPEN]` for owner confirmation.

### Delta 27 — settings shell

Add one project-scope “AI settings” destination in the settings rail/mobile select (matching the sidebar id, not duplicating it) and a static entry specimen with the current settings version; add tenant opt-in status (on/off/unavailable) with its policy source. Off explains that AI generation is blocked and manual authoring remains, and offers an admin link only when destination and permission are supplied — otherwise “contact an administrator”, never a dead URL.

Order is phase filter, then permission filter, then first-accessible-destination selection. A G4 tab selected when the build/phase is lowered must not leave its content visible or selected. Direct access never bypasses scope or permission. `ai-settings` remains `[OPEN]` `OI-13-509` until its gate is `authoring:read`; the page also needs `project:write` for Save, rendered explained-disabled when absent.

### Delta 31 — usage and retention

Only the AI credit region is G1: org-scoped balance, period, as-of and G1 actions, behind `usage:read`. Resource usage, storage/ingest/egress quotas and retention stay G2 and keep their own warned/blocked semantics; storage warnings never become credit verdicts. The Settings `quota-retention` tab stays a placeholder with no balance, so the org balance exists once. Full field/state treatment: [authoring-usage-credits.md](authoring-usage-credits.md).

## States

Section 6 of the authoring plan defines one shared request machine (`idle → loading → ready{empty} | error{forbidden, not_found, rate_limited/unavailable, generic}`, plus `stale` and `offline-ish`) and lists **8 surface rows** (tree/table, editor, pending changes, generation, timeline, AI settings, import/export, usage credits). **Navigation is not one of those eight rows**, so there is no ninth surface row to cover here; the applicable states are the shared machine plus the shell states AC14 and B1-01 require. The usage row's own eight states are covered in the usage file, not duplicated here.

| State | Region behaviour and recovery | Mockup `?state=` / gap |
| --- | --- | --- |
| idle / first paint | Shell renders without inventing entries or counts; no flash of later-phase groups | [OPEN] no named specimen |
| loading identity/permissions | Skeleton or reserved nav space, `aria-busy` on the nav region, one polite status; do not render a guessed G4 shell | `loading` is a content specimen only; [OPEN] nav-identity loading |
| ready, populated | Five groups in journey order; only active-phase, permitted entries; current route marked `aria-current` | `default` |
| ready, no permitted entries | Keep shell and page content; explain that no destinations are available and who can help; never an empty unstyled column | [OPEN] shell `empty` is a content specimen, not a nav state |
| no search match | Keep search value, show a distinct no-match message; hidden entries never leak through search | [OPEN] shell `no-match` is a content specimen, not a nav state |
| load error | Inline error and Retry; preserve scope/route; no fabricated groups | `error` |
| 403 on a permitted-looking route | Forbidden page/panel for missing permission; no protected content behind it | `403` |
| 404 / later-phase deep link | Later phase = **unmatched** route, not a disabled page; unknown/foreign id = not-found without existence disclosure | [OPEN] shell declares no `404` specimen |
| 429 / 503 | Typed feedback with supplied `Retry-After`; Retry disabled while waiting; no countdown invention | [OPEN] no `429`/`503` specimen; shell `412` is a linked edit only |
| stale after failed refresh | Keep last permitted nav with an explicitly stale, dated state; never silently drop entries into a false “complete” nav | [OPEN] no specimen |
| permission-restricted nav | Hide unauthorized entries; typed URL yields forbidden (in scope) or not-found (foreign scope), never a permission-shaped page with data | `403`; [OPEN] restricted-entry specimen |
| phase G1 | Test design (5 entries) plus the 5 G1 Settings entries; every G2–G4 entry and group with no remaining entry is absent | review-only select at G1; production fixed by build |
| phase G2 | Adds Scripts & runs (4) and the G2 Settings entries (3); G3/G4 still absent | review-only select at G2 |
| phase G3 | Adds Learn & repair (4); G4 still absent | review-only select at G3 |
| phase G4 | Adds Release (6) and the G4 Settings entries (3); all groups populated in journey order | review-only select at G4 |
| phase change interaction | Filtering happens before rendering; focus stays on the phase control and never lands on a removed entry; no permission is granted by the switch | shared review JS; production has no switch |
| collapsed rail / mobile drawer | Rail keeps accessible names; drawer is modal: focus enters close control, Tab contained, Escape closes, focus returns to toggle | `loading`/`default` narrow captures; [OPEN] explicit drawer state |

The shell's `412` is a linked edit demonstration, not a navigation read result; the shell is read-only and never invents a save. Focus order: skip link → navigation toggle/search as applicable → group headings and entry links in DOM order → topbar scope/account → main heading → page actions. A phase/permission change keeps focus on the initiating control when it remains visible; otherwise focus moves to the nearest stable heading, never to a removed link. One polite live region announces meaningful nav/phase changes; entry lists are real lists of links, not menus of buttons, and `aria-current` is set on exactly one entry.

## Copy length (vi vs en)

Only group labels and the B1-01 preserves are authoritative translations; entry labels not quoted below must come from `messages.vi.xlf` and are `[OPEN]` for this sheet rather than machine-translated here. Natural wrapping, no ellipsis-only labels, no shrinking type; rail uses the same localized accessible name.

| Purpose | vi | en |
| --- | --- | --- |
| Group 1 | Thiết kế kiểm thử | Test design |
| Group 2 (longest vi specimen) | Kịch bản và lượt chạy | Scripts & runs |
| Group 3 | Học và đề xuất sửa | Learn & repair |
| Group 4 | Phát hành | Release |
| Group 5 | Cài đặt | Settings |
| Review-only phase control | Giai đoạn | Phase |
| Authoring entry | Cây chức năng | Feature tree |
| Generation entry | Tạo kiểm thử | Generate tests |
| Pending-changes entry (long entry specimen) | Thay đổi chờ áp dụng | Pending changes |
| AI-settings entry | Cài đặt AI | AI settings |
| Usage entry | Mức sử dụng và tín dụng AI | Usage and AI credits |
| No destinations | Không có điểm đến nào bạn có thể truy cập. Liên hệ quản trị viên tổ chức. | No destinations are available to you. Contact an organization administrator. |
| Restricted entry recovery (longest sentence specimen) | Bạn không có quyền mở mục này. Quyền do quản trị viên tổ chức cấp; địa chỉ trực tiếp không bỏ qua kiểm tra quyền. | You do not have permission to open this entry. An organization administrator grants access; a direct URL does not bypass the check. |

The group labels are immutable user-visible vocabulary: no re-translation, no diacritic stripping, no “Authoring” substitute for “Test design”. Kiểm thử (not kiểm tra) is intentional.

## Responsive notes

- **≥1280 px:** expanded navigation, 224 px; labels, scope and account text visible. Content cap 1440 px with 24 px gutters; one visible `h1`.
- **768–1279 px:** icon rail, 68 px; labels visually hidden but available to AT; scope/account text hidden only visually. Content gutter 20 px. No entry may become an unlabelled icon button.
- **<768 px:** closed off-canvas drawer, normally `min(84vw, 320px)`; scope hidden, topbar search on its own row, page header/actions stack. Drawer is modal and returns focus on Escape/close. Read-only applies to editing/applying, not to navigation, search, period or inspection.
- **≤420 px / 360 px requirement:** 100vw drawer, 12 px gutters, no page-level horizontal scroll, full-width actions where declared. No clipping with `overflow-x: hidden`; long vi labels wrap.

| Manual WCAG 2.2 check | Required run and expected result | Evidence status |
| --- | --- | --- |
| 2.5.8 target size | Shell + five deltas, 1280/360 px × light/dark × vi/en: every entry, toggle, disclosure, select and footer action ≥24×24 px; coarse pointer 44×44 px | ⚠️ not run |
| 2.4.11 focus not obscured | Same matrix: focused nav entry/control never under sticky topbar, drawer header or toasts; drawer scroll clears chrome | ⚠️ not run |
| 2.5.7 dragging | Same matrix: all navigation by link/button/keyboard; no drag-only sidebar or phase control | ⚠️ not run |
| 2.4.3, 2.4.7, 1.4.11 focus order/visible focus/contrast | Same matrix: logical order incl. drawer, 2 px ring + 2 px offset unclipped, boundaries/text ≥ required contrast in both themes | ⚠️ tokens referenced, not measured |
| 1.3.1, 4.1.3, 1.4.10 structure/status/reflow | Same matrix: lists of links, one `aria-current`, one polite phase/nav announcement, 360 px no page overflow | ⚠️ not run |
| 2.4.1 bypass / 2.1.1 keyboard | Skip link first; whole shell and every delta operable without a pointer | ⚠️ not run |

## Invariants

- Phase visibility is **hidden and unmatched**, never disabled, greyed or teased. A later-phase entry contributes no heading, count, teaser or search hit; a later-phase deep link is not a page.
- Hidden is never authorization. The server remains authoritative; a permitted-looking entry still needs its permission, and a missing `usage:read` means no credit entry, no protected request and `/forbidden` on a typed URL. Foreign-tenant ids use not-found, not forbidden.
- One registry, one order: five group labels in journey order, unchanged diacritics, no phase switch in production, no hand-written sidebar per page, no duplicate row for a parent's child screens.
- No fabricated active item, no throw and no silent redirect when the current entry is filtered out. Focus moves to a stable target; it is never left on an invisible node.
- Phase and permission filtering run before destination selection; a lowered phase cannot leave hidden content visible or a stale selection. Scope changes re-derive destinations.
- No release verdict, waiver, CI-health panel or policy link on G1–G3. The home shows authoring state only; the balance lives once, on the org usage page.
- Absent counts, coverage and credit values are unavailable (“—”), never `0`; no count is inferred from a partial page. Untrusted names/labels are escaped text, never HTML.
- No write, `If-Match` or `Idempotency-Key` belongs to navigation. Linked edits elsewhere keep their own tags; a missing tag is the server's 422 and a stale one 412.

## Open points

| Gap | Recommended default / decision owner |
| --- | --- |
| [OPEN] B1-03 four G1 onboarding steps conflict with binding UI62 (all five steps) | Keep five shipped steps and map the G1 milestones onto them; owner-approved amendment required before renumbering. Owner + 13.4.1-T5. |
| [OPEN] B1-02 duplicates a numeric credit balance on the home while AC14/T7.2 places it once behind `usage:read` | Home carries a scoped link/status, not a second balance; owner confirms. 13.4.1-T7.2 / 09.7.1. |
| [OPEN] `OI-13-509`: registry `ai-settings` gate is `project:read` while its reads need `authoring:read`; Save needs `project:write` | Re-gate in 13.4.1-T5.1 plus registry spec; unauthorized Save explained-disabled. Owner 09.1.1 registry. |
| [OPEN] `OI-13-503`: pending-change counts have no tree/manual-case row projection | Add `pending_change_count`; render “—” when absent, never `0`. 13.1.1-T2 with 13.1.2. Coverage is already supplied by `ManualCaseListItemDto.linked_live_count`/`linked_case_ids`; see [authoring-tree.md](authoring-tree.md#v2-pages). Do not include coverage in this open item or invent aggregate coverage totals. |
| [OPEN] `OI-13-504`: no AI-credit schema; page 24/31 credit fields are mockup-only | Usage page owns the single balance once the contract lands; see the usage file. 08.5.5-T5.2. |
| [OPEN] Nav search over hidden entries, and account-menu identity source | Recommend a shared phase+permission predicate for search, and trace the session identity read before implementing. 09.1.1-T4. |
| [OPEN] Shell declares no `404`, `401`, `429` or stale specimen, and no explicit drawer/restricted-nav capture | Add to the later approved capture set; do not invent `?state=` names now. Design review owner. |
| [OPEN] Review and approval | Independent observations and docs-only dispositions are recorded in [authoring-review.md](authoring-review.md). Remaining reference/runtime and visual/AT evidence precede owner approval; proposed status authorizes no implementation start. No new checks were run in this correction assignment. |
