# UI format rules for implementers

## Purpose and precedence
[DECISION] Apply this baseline to implemented Evidgate UI and shared components; unbuilt stub screens are plan-only this round. This file refines, never overrides, [AGENTS.md](../../AGENTS.md), [00 §5.6](../../plans/00-overview.md) and accepted [ADR-0011](../adr/0011-spartan-tailwind-foundation.md).
[DECISION] Owner-approved format, 2026-11-14: single-line compact status labels; soft 4px corners and shared status icons; compact/regular minima 24/32px; localized “Not evaluated”/“Chưa đánh giá”; column budgets and two tags plus `+N`; stacked narrow records, with scroll only for an approved comparison screen; frozen route-to-mockup matrix. Onboarding step order and API-token scope/reveal behavior retain their current contracts.
The approved format above supersedes older wrapping/pill/pending-copy examples only; preserve the design references' screen hierarchy, responsive structure and all higher feature contracts. A mockup match never grants a permission, changes an API or proves a feature delivered.
Legend: ✅ verified with a source link · ⚠️ unverified evidence · `[INFERENCE]` deduced, not verified · `[DECISION]` accepted project/owner policy · `[OPEN]` unresolved owner decision. Numeric product budgets are policy, not universal WCAG/Carbon requirements. Rules are reviewable requirements, not a claim that current screens already pass.

## Component usage — mandatory framework
**UI1** — Use Brain 1.9.0 behavior with locally owned Helm in `apps/web/src/app/ui/`, imported through `@evidgate/ui/<family>`, and Tailwind 4.3.4 on `--eg-*` via `spartan-theme.css`; **check:** compare imports, dependency pins and theme aliases with ADR-0011.
**UI2** — Exclude hand-rolled button/badge/card/table/select/dialog skins, ad-hoc native styled controls where a Helm family exists, raw colour literals, a second styled library and global Preflight; **check:** inspect changed templates/CSS/dependencies, treating `.badge`/`.chip` names as candidates rather than proof and classifying genuine Helm triggers/structural scrims separately.
**UI3** — Reuse shared domain components before direct primitives: RunStatePill/StatusPill, EvidenceBadge/OutcomePill/DecisionChip, HashChip/RevisionChip, DataTable/DataTableValue and shared state/conflict patterns; **check:** compare the nearest `shared/ui/**` precedent and reject a feature-local semantic or formatting duplicate.
**UI4** — Add a missing family only through ADR-0011's published `@spartan-ng/cli@1.9.0` manual-copy route with provenance/licences, matching Brain behavior, owned facade, tokenized recipe and static-icon adaptations, obtaining an ADR amendment outside its retained set; **check:** inspect the source/template provenance and avoid a permanent CLI dependency or invented widget.
**UI5** — Use `button[hlmBtn]` for actions and `a[hlmBtn]` for button-styled navigation with default primary, outline/secondary secondary, ghost utility, destructive confirmed risk and link text-style variants; **check:** prefilter `<button`/button-like anchors without `hlmBtn`, then exclude genuine tabs/menu triggers; ordinary inline links remain semantic anchors.
**UI6** — Use `[hlmInput]` with `[hlmLabel]` for text/number/textarea fields, the full `hlm-select`/multiple/trigger/content/item family for choices, and `hlm-checkbox` for boolean/indeterminate selection; **check:** inspect field imports/selectors and associations, flagging native `<select hlmInput>` as a migration candidate rather than a Helm select.
**UI7** — Use owned Helm dialog/portal/content/title/description/footer/close or `HlmDialogService` on Brain for modal interaction with one focus-trap lifecycle; **check:** verify `@evidgate/ui/dialog`, translated close names, Tab/Escape/focus return and absence of a parallel hand-written/CDK Dialog implementation; ADR-0011 is binding.
**UI8** — Use the `dropdown-menu` family for overflow/actions, including its checkbox/radio and destructive item variants where appropriate; **check:** inspect trigger/menu/item composition and keyboard behavior rather than generic clickable divs.
**UI9** — Use HlmTabs/list/trigger/content with matching keys and explicitly set `hlmTabsList` to `[variant]="'line'"` until the reported 2.66:1 dark contrast of `default` is fixed, reserving tabs for in-page panels; **check:** inspect the list binding and keyboard behavior, distinguish route navigation, and require contrast evidence before restoring `default`.
**UI10** — Use `HlmTableContainer` plus native table/HlmTable and matching head/body/row/header/cell/caption helpers for ordinary tables while retaining shared virtualized `app-data-table` above 200 loaded rows; **check:** inspect `<table>` without `hlmTable` and test the >200-row boundary without replacing the CDK data-table contract.
**UI11** — Use HlmCard default/sm with its header/title/description/content/action/footer helpers only for genuinely bounded groups; **check:** inspect card-like wrappers and preserve plain sections instead of turning every screen into nested cards.
**UI12** — Use HlmAlert default for neutral/informational feedback and destructive for error, composed through existing shared state wrappers when available; **check:** inspect alert/title/description/action imports and the rendered foreground/background pairing.
**UI13** — Use HlmToaster with Brain `toast`, HlmSkeleton for placeholders, HlmTooltip for supplemental hints, HlmSeparator for dividers, HlmIcon fixed compiled geometry and `utils` class merging for their respective needs; **check:** reject alternate toast/shimmer/tooltip/icon runtimes and arbitrary SVG strings, and reserve DataTableValue disclosure for clipped essential values.

## Chips, badges and tags
**UI14** — Preserve independent lifecycle, evidence completeness, test outcome and release decision using the shared localized vocabulary below and exact PASS/FAIL/INCONCLUSIVE tokens, with neutral pending displayed as “Not evaluated”/“Chưa đánh giá” and raw `pending` available in provenance; **check:** compare fixtures for pending, missing, unknown, incomplete and failing states without an inferred success fallback.
**UI15** — Limit authored compact/regular chip labels to 16/20 graphemes respectively in each locale on one line, with dimension context supplied by the column/label and full explanatory wording outside the chip; **check:** count displayed graphemes including spaces/punctuation, inspect `white-space:nowrap`, and never substring user-supplied values to meet a copy budget.
**UI16** — Apply the shared compact/regular badge recipes: minima `--eg-space-7`/`--eg-control-sm` (24/32px), compact `--eg-text-xs`/`--eg-leading-xs`, regular `--eg-text-sm`/`--eg-leading-sm`, compact vertical/horizontal `--eg-space-1`/`--eg-space-3` and regular `--eg-space-2`/`--eg-space-4` insets, `--eg-weight-semibold`, gap `--eg-space-1`, radius `--eg-radius-sm` and leading decorative HlmIcon for statuses; **check:** inspect computed geometry, diacritic clearance and identical shared recipes across screens rather than inventing a `HlmBadge size` input.
**UI17** — Pair HlmBadge secondary domain recipes or outline verdict/identity recipes with matching `--eg-success-fg/bg`, `--eg-danger-fg/bg`, `--eg-warning-fg/bg`, `--eg-info-fg/bg`, `--eg-neutral-fg/bg` or independent `--eg-pending-fg/bg/border` roles; **check:** inspect both themes, text-plus-icon meaning and neutral pending/unavailable rather than raw palette classes or one generic destructive badge.
**UI18** — Bound table chips by `min(100%,24ch)` with a shrinkable one-line label, nonshrinking icon, one domain status per status cell and recoverable full text only when actually clipped; **check:** stress long localized labels and confirm that chip wrapping cannot add row lines while adjacent reasons/details remain complete.
**UI19** — Show at most two category tags on one table-cell line followed by an exact labelled `hlmBtn ghost` `+N` disclosure, allowing collections to wrap only outside tables; **check:** inspect hidden-count accuracy and every value through keyboard/tap, Escape and focus return.

## Data tables
**UI20** — Declare column type/priority/overflow using the budgets below, flex the subject, keep utilities intrinsic and align text/status/IDs/timestamps start and magnitudes plus headers end with tabular numerals; **check:** inspect computed widths/alignment and long actual values rather than equal column percentages.
**UI21** — Preserve `--eg-row-compact`/`--eg-row-default` 40/44px minima with horizontal `--eg-space-4` and vertical compact/default `--eg-space-2/3`, using default density and 44px targets on coarse pointers; **check:** measure row/control rectangles and permit non-virtual narrative rows to grow.
**UI22** — Keep fixed-height virtual rows single-line and recoverable while recording **[OPEN] owner decision**: Runs rich-cell tables now page loaded rows at 200, whereas ADR-0011 line 75 requires lists above 200 to remain virtualized; **check:** compare the Runs/shared-table sources and 200/201-row behavior without treating pagination as an approved exception or resolving this conflict here.
**UI23** — Scope sticky table headers to their named local scroller with opaque `--eg-surface`, `--eg-z-sticky` and a separator, retaining shared header behavior; **check:** scroll and Tab through the region without a focused value/control hidden under the header/topbar.
**UI24** — Separate shared DataTable container-width stacking (layout only) from viewport `NarrowMutationPolicy`: narrow desktop panels retain `mutates: true` columns, and only viewport policy removes them below 768px; **check:** compare a narrow container at desktop width with 767/768px viewports, preserving inspection facts, the existing below-480/≤420px refinements and comparison-only approved scroll.
**UI25** — Retain table headers/context with a full-span or associated shared loading/empty/error state rather than fake rows, silent failure or empty-success; **check:** activate each state fixture and inspect the caption association plus one meaningful announcement outside the grid.
**UI26** — Place supported page size, honest loaded/range text and Previous/Next below the table with default 50 and supported 25/50/100/200 choices, preserving opaque cursors and reset-on-server-sort/filter; **check:** verify endpoint capabilities, no invented totals/page jumps and pagination outside any table scroller.
**UI27** — Place discoverable keyboard sorting with `aria-sort` in headers, search/filters/reset above data and consistent trailing HlmButton/menu row actions with any existing batch scope explicit; **check:** operate without hover, inspect last-column alignment and distinguish server filtering from labelled local-only filtering.

## Long content
**UI28** — Make flex/grid text shrink through `min-w-0` and `minmax(0,1fr)`, preserving natural Vietnamese/name word wrapping and `overflow-wrap:anywhere` only as the long-token escape hatch; **check:** stress names/emails/URLs/branches/IDs without global `break-all` or page-level overflow clipping.
**UI29** — Preserve complete accessible values through DataTableValue-style keyboard/pointer/touch disclosure or expansion, limiting scan summaries to one line and description previews to two while leaving essential instructions/errors/reasons unclamped; **check:** inspect full text, persistent hover/focus, tap, Escape and a non-clipped overlay rather than native `title` alone.
**UI30** — Present machine IDs/hashes through `CopyableIdentifierComponent` with mono, useful middle elision and shared copying of safe original values (UI58), keeping human names unmodified and full provenance/destinations available in details/confirmations; **check:** compare safe clipboard values with the original and verify refusal for format controls rather than copying an invisible or silently sanitized identifier.

## Page anatomy
**UI31** — Order context, one visible h1, description and content through shared page/section headers, projecting actions with `appPageHeaderActions`/`appSectionHeaderActions` and `HeaderActionsDirective`; **check:** inspect the matching projection attributes, at most one primary task action and trailing-desktop/below-title-narrow placement without production mockup-review controls.
**UI32** — Preserve the inclusive 1440px content cap, desktop/tablet/narrow/≤420px gutters 24/20/16/12px, 24px `--eg-space-7` section rhythm and tokenized inner groups; **check:** inspect bounds and computed gaps against `--eg-space-*`, remembering Tailwind `gap-2` is 8px but `--eg-space-2` is 4px.
**UI33** — Require Helm/Brain `alertdialog` consent for destructive or credential-changing actions, including token/secret rotation, with named consequences, Cancel initially focused and no request before explicit confirmation; **check:** inspect role/focus and network calls through opening, Cancel, Escape and confirmation, reserving `hlmBtn destructive` for the confirmed operation.

## Forms
**UI34** — Associate persistent above-field sentence-case HlmLabel, one consistent required/optional convention, below-control hints and specific inline errors with native/programmatic required/invalid state and `aria-describedby`; **check:** inspect accessibility associations and retained hints rather than placeholder-only or red-border-only errors.
**UI35** — Size HlmInput/HlmSelect fields by expected content inside a bounded form column with related pairs stacking and textareas growing naturally; **check:** stress a long email/URL, selected option and 40% localized expansion without fixed labelled-control widths or truncated option text.
**UI36** — Validate touched fields on blur and during correction with authoritative submit validation, retained input and a focused linked summary after failed submit; **check:** submit invalid data, follow every summary link and reject focus jumps on each blur or duplicate submitting/announcement behavior.
**UI37** — Distinguish readable/selectable read-only data from disabled unavailable interaction with an adjacent prerequisite explanation; **check:** select/copy immutable text, inspect contrast and reach the explanation without focusing a disabled control.
**UI38** — Keep page-form Save/Continue then Cancel start-aligned, dialog Cancel then commit end-aligned and wizard Back then Continue, with equal DOM/visual order when wrapped; **check:** Tab the rendered footer at narrow width and inspect the HlmButton variants.

## Detail facts
**UI39** — Group key-value facts with semantic `dl/dt/dd`, aligned wide labels/values and stacked narrow labels, preserving distinct Not set/Not available/Not applicable plus real zero/false; **check:** inspect semantics and missing-data fixtures rather than an unexplained dash or retrieval-failure zero.
**UI40** — Format operational instants with Angular locale-aware date formatting in explicitly labelled UTC and absolute date/time for audit/evidence/release, keeping raw RFC3339 reachable and relative time supplemental; **check:** compare vi/en and keyboard/touch recovery without timezone-shifting date-only values or hand-built ambiguous dates.
**UI41** — Format exact counts and durations with locale-aware numbers, units and tabular numerals, using ms below 1000ms and seconds with at most two fractional digits/no unnecessary zeros above it; **check:** inspect 0/999/1000ms, large counts and missing duration without compact-only precision or invented zero.

## Feedback states
**UI42** — Reuse shared empty/loading/error/forbidden/not-found/conflict patterns with scoped persistent inline recovery for blocked work and toast only for non-blocking confirmations; **check:** exercise first-use versus filtered no-results, service failure, denial and 412 draft retention without toast-only validation or automatic conflict resubmit.
**UI43** — Reserve stable content-shaped skeleton geometry or local action progress with the affected region busy, decorative placeholders hidden from AT and one contextual live announcement without focus theft; **check:** inspect loading/layout shift/reduced motion and keep static pending verdicts free of spinners/live-region spray.
**UI44** — Retain successful partial data, explicit stale/failed-section context and existing foreign/unknown-scope Not found versus in-scope forbidden behavior; **check:** activate partial/permission fixtures without dropped sections, forbidden detail leaks or misleading complete counts.

## Typography and density
**UI45** — Use existing display/body/mono families with h1 2xl 28/36, section h2 xl 22/30, card h3 lg 18/26, body md 16/24, tables/forms sm 14/20 and metadata xs 12/18 token roles independently of semantic heading level; **check:** inspect computed `--eg-text-*`/`--eg-leading-*` values and Vietnamese diacritic clearance without new fonts.
**UI46** — Use regular/medium/semibold/bold token weights by role, tabular numerals for comparable magnitudes and a 65–75-character prose measure with shared density variants rather than route-local type shrinking; **check:** inspect computed weights, a preferred `65ch` prose cap and unchanged table/code width.

## Navigation
**UI47** — Preserve registry grouping/phase/permissions with expanded 224px sidebar at ≥1280px, 68px rail at 768–1279px and modal drawer below 768px, keeping current-location, focus and keyboard labels distinct; **check:** inspect `aria-current`, existing user override, skip link, drawer Escape/focus return and measured topbar focus-scroll margin.
**UI48** — Keep scope switchers, route and displayed facts synchronized with full option access, hierarchical breadcrumbs and localized shell locale/theme controls that preserve safe context; **check:** switch long-named scopes/languages/themes and inspect current/ancestor identity without stale facts or unlabelled rail controls.

## Theme, i18n and accessibility
**UI49** — Use semantic `--eg-*` pairs/Spartan aliases in both themes with explicit foregrounds on painted control surfaces and rendered contrast ≥4.5:1 normal text, ≥3:1 large text and necessary UI boundaries/icons; **check:** measure actual composite states rather than assuming token/opacity inheritance proves contrast.
**UI50** — Keep visible `--eg-focus` 2px width/2px offset unclipped and targets at least 24×24px or a documented WCAG exception, applying the stronger 44×44px coarse-pointer policy with 8px gaps where feasible; **check:** measure focused/target rectangles and obscuration rather than colour alone.
**UI51** — Localize visible/accessibility copy through `@angular/localize` while preserving provenance enums/IDs and allowing 40% expansion without fixed labelled widths or smaller type; **check:** inspect vi/en, document language, full labels/options and text-with-icon semantics in dark/light.
**UI52** — Preserve content/function at 320px reflow and 200% text with spacing overrides, using existing 150/200ms motion tokens and disabling nonessential motion under reduced motion; **check:** apply 1.5× line, 2× paragraph, 0.12em letter and 0.16em word spacing together, restricting two-dimensional exceptions to genuine approved table regions.

## Mockup conformance
**UI53** — Match each implemented screen to the frozen matrix below and shared `layout.md`/`tokens.md`/`state-sheet.md` plus its named flow, preserving content hierarchy, task action and scoped feedback; **check:** compare 390/768/1440px reference structure without fixture text, illustrative data or static mockup interaction code.
**UI54** — Preserve screen-specific stack/split layouts, including the failure-inbox preview below the table, 320px context rails only where referenced and peer balanced splits stacking below desktop; **check:** compare the matching file rather than imposing a universal KPI-card or two-column template.
**UI55** — Treat retired, route-less and unbuilt destinations as plan-only this round while preserving current onboarding step and API-token scope/reveal contracts; **check:** distinguish a registered stub/reference target from an implemented screen and record a plan-only finding instead of creating new routes or reviving retired Studio.

## Redesign safeguards and verification
**UI56** — Reuse `CopyValueButtonComponent.size: 'sm'|'lg'` and `CopyableIdentifierComponent.copySize` instead of local copy styling, selecting `lg` for 44×44px targets on every pointer while both sizes retain coarse-pointer 44×44px minima through `--eg-control-touch`; **check:** inspect size propagation in the preview/full-value dialog and measure both target dimensions.
**UI57** — Re-check `NarrowMutationPolicy.allowsMutation()` in mutation handlers and dialog submits immediately before side effects, alongside authorization/scope checks rather than instead of them; **check:** resize desktop→narrow with a dialog open and verify no request, without relying only on hidden DOM or a previously read `canMutate` signal.
**UI58** — Render server-supplied identifiers through `escapeFormatChars` from `shared/ui/values/ref-name-display.ts` inside `<bdi>`, preserving the shared copy action's refusal of Unicode format/bidi controls; **check:** inspect visible `⟨U+…⟩` tokens and bidi isolation, with no clipboard write for unsafe originals and no silent stripping or replacement copy.
**UI59** — Protect a revealed one-time secret from Escape/backdrop dismissal using the shared reveal/dialog guard until explicit dismiss-and-clear; **check:** verify Escape preserves the displayed value and reveal slot, while explicit dismissal clears both and restores focus.
**UI60** — Run the assigned `scripts/verify.sh` web stages plus `npm run e2e:fixtures` and `npm run e2e:core` from `apps/web` for a UI change; **check:** record both complete browser-suite results on the reviewed head, because CI invokes them separately and `verify.sh` does not include them.
**UI61** — Show a four-digit year in the calendar part of every user-visible date and date-time, in every locale, with Vietnamese calendar parts as `dd/MM/yyyy`; obtain date/date-time patterns from the single shared producer `apps/web/src/app/shared/time/short-date-format.ts`, never formatting dates independently in a component; **check:** compare exact vi/en date-only and date-time output including second-precision operational timestamps, preserve locale order/separators, clocks, UTC labels and original ISO values, and reject local formats or two-digit-year aliases.

## Compact vocabulary (UI14–UI15)
[DECISION] Own short-copy catalog IDs centrally in the existing domain component; retain its current full descriptions outside chips, using the same short form for a regular label that exceeds 20 graphemes. A missing/unknown/read-failure explanation and raw supplied value remain outside the badge, not silently converted into a known state.
| Dimension / supplied state | English | Vietnamese | Tone |
|---|---|---|---|
| Lifecycle: created | Created | Tạo mới | neutral |
| Lifecycle: queued | Queued | Đang chờ | neutral |
| Lifecycle: running | Running | Đang chạy | info |
| Lifecycle: collecting | Collecting | Đang thu thập | info |
| Lifecycle: complete | Complete | Hoàn tất | success |
| Lifecycle: incomplete | Incomplete | Chưa đủ | warning |
| Lifecycle: canceled | Canceled | Đã huỷ | neutral |
| Evidence: complete | Complete | Đủ | success |
| Evidence: incomplete | Incomplete | Thiếu | warning |
| Outcome: all_passed | All tests passed | Tất cả đạt | success |
| Outcome: has_failures | Has failures | Có lỗi | danger |
| Outcome: no_results | No results | Chưa có kết quả | neutral |
| Decision: PASS / FAIL / INCONCLUSIVE | Same canonical token | Same canonical token | success / danger / warning |
| Decision: supplied pending | Not evaluated | Chưa đánh giá | independent pending (neutral) |
| Any dimension: missing/unknown/unavailable read | Unavailable | Không khả dụng | neutral; preserve the actual distinction outside |

## Column budgets (UI20)
[DECISION] Treat these as min→max review budgets for non-virtual tabular mode, not new tokens or fixed failure-inbox widths; allow actual digits/units to exceed a budget rather than clip them. Retain existing 640px generic/900px workbench minima only in tabular mode, not the stacked narrow layout.
| Type | Budget | Overflow/priority |
|---|---|---|
| Subject/name | 9.5rem→remaining; secondary 12→32ch | P1 subject, recoverable scan ellipsis; wrap stacked values. |
| Status/tag | approved label + icon/insets→24ch per chip | P1 state/decision; explanation outside, tag group intrinsic. |
| Hash/ID/branch | 12→28ch | P2 unless primary identity; ID/hash mono, useful middle elision. |
| Email/URL | 20→36ch | P2, recoverable table elision; full detail/confirmation wrapping. |
| Count/duration | 8→14ch, extend for actual values | P2, no clipped digits/units. |
| Date/time | 18→26ch | P2, explicit date/time split permitted, UTC retained. |
| Selection/actions | 44px selection minimum; intrinsic actions | P1 actions, never hover-only or compressed below target policy. |

## Frozen route-to-mockup matrix (UI53–UI55)
[DECISION] `O=/orgs/:orgId`, `P=O/projects/:projectId`; paths below are relative to [web-ui/output](../design/web-ui/output/NAV.md), companion flows to [web](../design/web/README.md). Registration is not delivery proof; unbuilt destinations remain plan-only, and route aliases do not imply equal permission/data scope.
| Registered surface | Exact HTML reference; companion flow | Preserve |
|---|---|---|
| `/login` | `02-login/evidgate-sign-in.html` | Standalone sign-in, return context. |
| `P/overview`; shared shell | `24-project-connection/evidgate-project-overview.html`; `01-app-shell/evidgate-workspace-shell.html` | Scope/authoring home, recent versions; sidebar/topbar/content shell only. |
| `O/onboarding[/:projectId]` | `23-onboarding-wizard/evidgate-onboarding.html`; `onboarding.md` | Progress/active step/resource ledger; current step order and G2 continuation. |
| `P/runs` | `03-run-list/evidgate-runs.html` | Filters, run table, pagination; separate dimensions. |
| `P/runs/:runId` | `04-run-detail/evidgate-run-detail.html`; `run-detail.md` | Facts, attempts, evidence workbench, timeline/context. |
| Evidence/decision regions of that detail | `05-evidence-panel/evidgate-run-detail.html`; `06-decision-waivers/evidgate-run-decision.html`; `run-detail.md` | Frozen revision/producers/missing reasons, pinned stored assessment; not new routes/waiver scope. |
| `P/failures` | `08-failure-inbox/evidgate-failure-inbox.html`; `failure-inbox.md` | Filters/table, selected preview below. |
| `P/repairs` | `12-healing-queue/evidgate-healing-review.html`; `healing-shadow-review.md` | Availability/shadow context, queue, selected proposal. |
| `P/knowledge` | `15-knowledge-revocation/evidgate-knowledge-lineage.html` | Failure, source chain, effect/dependents. |
| `P/availability` | `16-healing-availability/evidgate-healing-availability.html` | Effective state before hierarchy/configuration. |
| `P/studio` | `17-studio-template-gallery/evidgate-test-studio.html` | Retired reference only; no revived editor. |
| `P/suites` | `20-suite-composition/evidgate-suite-composition.html` | Revision/variants, dependencies, coverage context. |
| `P/releases` | `21-release-view/evidgate-release-view.html`; `release-view.md` | Stored decision, component matrix, pinned context. |
| `P/quality` | `22-quality-overview/evidgate-quality-overview.html` | Project/period, distinct metric definitions. |
| `P/catalog` | `25-test-catalog/evidgate-test-catalog.html` | Catalog/uncovered cases, identity history. |
| `P/mapping` | `26-mapping-resolution/evidgate-mapping-resolution.html` | Unresolved mappings, selected report/provenance. |
| `O/settings` | `27-settings-shell/evidgate-settings.html`; `settings.md` | Scope, gated destinations, active body. |
| `O/members`; `O/settings/members` | `28-members-roles/evidgate-members-roles.html`; `settings.md` | Member/invitation table, role matrix, dialogs. |
| `P/secrets`; `O/settings/secrets` | `29-secret-references/evidgate-secret-references.html`; `settings.md` | Metadata/lifecycle, rotation context. |
| `P/policies`; `O/settings/policy` | `30-policy-revisions/evidgate-policy-revisions.html`; `settings.md` | Revision navigation/detail, activation/quarantine. |
| `O/usage`; `O/settings/quota-retention` | `31-quota-retention/evidgate-usage-retention.html`; `settings.md` | Credits, action usage, storage and server quota state. |
| `O/audit`; `O/settings/audit` | `32-audit-log/evidgate-audit-log.html`; `settings.md` | Filters, integrity, activity/detail, absolute UTC. |
| `O/security`; `O/settings/security-events` | `33-security-events/evidgate-security-events.html` | Scoped activity and safe details. |
| `O/agents`; `O/settings/agents` | `34-agent-pools/evidgate-agents-pools.html` | Pool rail, capabilities/registration/health. |
| `P/integrations`; `O/settings/integrations` | `35-integrations-webhooks/evidgate-webhook-integrations.html` | Endpoints/detail, delivery history, metadata boundary. |
| `O/support` | `36-support-access/evidgate-support-access.html` | Grant summary/list, scoped expiry/revocation. |
| `O/tokens`; `O/settings/api-tokens` | `37-api-tokens/evidgate-api-tokens.html` | Metadata/lifecycle; existing distinct token/scope/reveal contracts. |
| `P/deploy` | `38-deploy-targets/evidgate-deploy-targets.html` | Targets/promotions, separate release decision. |
| `P/authoring` | `40-authoring-tree/evidgate-authoring-tree.html` | Hierarchy rail, selected cases, separate tree/table feedback. |
| `P/pending-changes` | `42-pending-changes/evidgate-pending-changes.html` | Queue, before/after, batch reason, last-applied context. |
| `P/generation` | `43-generation/evidgate-generation.html` | Requirement/scope, preflight, progress, pending-human-review checklist. |
| `P/ai-settings` | `44-ai-settings/evidgate-ai-settings.html` | Project settings/guidance, template confirmation, save. |
| `P/scripts` | `46-generated-scripts/evidgate-generated-scripts.html` | Testability/delivery context, scripts, build/lint/source state. |
⚠️ No separate current route is asserted for reference directories `07`, `09`–`11`, `13`–`14`, `18`–`19`, `41`, `45`, `47`; consult the [reference index](../design/web-ui/output/NAV.md) for their exact files before any future plan. `00`/`39` are review tools, not product destinations. Retired Studio references do not authorize new authoring work.

## Before you yield a UI change
- Read the matching rule IDs, current feature contract, ADR-0011, shared primitive and exact screen reference before editing; cite those IDs and any plan-only finding in the handoff, not in source comments.
- Read and **run** the installed ui-ux-pro-max `scripts/search.py` for the concern (for example `python3 "$UUPM/scripts/search.py" 'badge chip label wraps' --domain ux -n 3`, plus relevant `--stack angular` guidance), with `$UUPM` set to the installed skill directory; record the exact commands, applied/rejected result rows and reasons; never use `--persist` or substitute its palette/framework for the project.
- Open the implemented screen at **390/768/1440px × dark/light × vi/en** with long names/emails/URLs/IDs/branches and 40% copy expansion; compare both the rules and its mockup, recording screenshots/selectors and observed failures rather than claiming unrun coverage.
- Exercise default/hover/focus/active/disabled/read-only/loading/empty/error/success/partial states that apply, keyboard-only and touch disclosure, dialog focus/Escape, 320px reflow, 200% text, spacing overrides and reduced motion.
- Prefilter consumer HTML **and inline TypeScript templates** for `<button` without `hlmBtn`, native `<select>`, `<table>` without `hlmTable`, ad-hoc `.badge/.chip/.card` skins and raw colours; classify Helm tabs/menu triggers, shared virtual tables and structural scrims before calling a hit a defect.
- Run UI60's assigned web stages and **both** fixture/core browser suites through the assigned gate owner, recording complete results; a successful `verify.sh` run alone does not cover those CI suites.
- Cite UI IDs with the exact browser evidence and the project's assigned harness/gate results; mark unrun checks ⚠️ and leave final independent QA/review to the assigned gate owners rather than asserting token presence is verification.

## Sources
- ✅ Binding framework/provenance/variants: [ADR-0011](../adr/0011-spartan-tailwind-foundation.md); [Brain/Helm/Tailwind aliases](../../apps/web/src/styles/spartan-theme.css), [tokens](../../apps/web/src/styles/tokens.css), [owned primitives](../../apps/web/src/app/ui/), [shared UI](../../apps/web/src/app/shared/ui/).
- ✅ Binding project rules: [AGENTS](../../AGENTS.md), [00 §5.6](../../plans/00-overview.md), [agent code rules](agent-code-rules.md); route sources: [registry](../../apps/web/src/app/shell/nav/nav-registry.ts), [settings tabs](../../apps/web/src/app/features/settings/settings-tabs.ts), [routing](../../apps/web/src/app/app.routes.ts).
- ✅ Screen sources: [layout](../design/web/layout.md), [tokens](../design/web/tokens.md), [states](../design/web/state-sheet.md), [shared brief](../design/web-ui/prompts/00-shared-brief.md), [reference index](../design/web-ui/output/NAV.md), [flow index](../design/web/README.md); approved format/current contracts take precedence over conflicting old fixture recipes.
- ✅ Pattern research: [Carbon tags](https://carbondesignsystem.com/components/tag/usage/), [tables](https://carbondesignsystem.com/components/data-table/usage/), [forms](https://carbondesignsystem.com/components/form/usage/), [Primer PageHeader](https://primer.style/product/components/page-header/guidelines/), [Fluent semantic tokens](https://fluent2.microsoft.design/design-tokens); borrow behavior/hierarchy, not external dimensions/palettes.
- ✅ Accessibility: [WCAG 2.2](https://www.w3.org/TR/WCAG22/) — 1.3.1, 1.4.1/3/4/10/11/12/13, 2.1.1, 2.4.7/11, 2.5.8, 3.3.1/2, 4.1.3; [reflow interpretation](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html). Full runtime conformance remains an evidence task, not an external-library guarantee.
- ✅ Research tool: [ui-ux-pro-max](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill) UX and Angular searches; use the installed version read-only and record commands/results per change. Product/brand recommendations never override the binding foundation.
- ✅ Copy/header APIs: [copy button sizes and safe-copy refusal](../../apps/web/src/app/shared/ui/values/copy-value-button.component.ts), [identifier copySize/full-value rendering](../../apps/web/src/app/shared/ui/values/copyable-identifier.component.ts), [header action directives](../../apps/web/src/app/shared/ui/page-header/header-actions.directive.ts), [page projection](../../apps/web/src/app/shared/ui/page-header/page-header.component.ts), [section projection](../../apps/web/src/app/shared/ui/page-header/section-header.component.ts).
- ✅ Layout versus mutation: [container measurement](../../apps/web/src/app/shared/ui/data-table/data-table.component.ts), [viewport-filtered record columns](../../apps/web/src/app/shared/ui/data-table/data-table-records.component.ts), [NarrowMutationPolicy](../../apps/web/src/app/shared/ui/narrow/narrow-mutation-policy.ts), [dialog-submit guard](../../apps/web/src/app/features/settings/api-tokens/api-token-revoke-dialog.component.ts).
- ✅ Identifier safety: [escapeFormatChars/hasFormatChars](../../apps/web/src/app/shared/ui/values/ref-name-display.ts); `<bdi>` composition and refusal-before-clipboard are in the linked identifier/copy components, not an invitation to copy sanitized substitutes.
- ✅ Credential consent/reveal precedents: [rotation alertdialog and Cancel focus](../../apps/web/src/app/features/settings/secrets/rotate-confirm-dialog.component.ts), [confirm-before-request boundary](../../apps/web/src/app/features/settings/secrets/secrets.page.ts), [one-time reveal Escape guard](../../apps/web/src/app/features/settings/secrets/rotate-reveal-dialog.component.ts).
- ✅ Tabs API: [list variant input](../../apps/web/src/app/ui/tabs/lib/hlm-tabs-list.ts), [line/default recipe](../../apps/web/src/styles/helm/tabs.css); ⚠️ UI9 records the dark-default contrast failure measured by the redesign's browser suite (2.66:1), not re-measured for this revision.
- ✅ Verification wiring: [CI browser suites](../../.github/workflows/ci.yml), [web commands](../../apps/web/package.json), [verify stages](../../scripts/verify.sh); the formatter stage uses root `format:check`, but [`.prettierignore`](../../.prettierignore) excludes `docs/**`, including this rule file.
- **[OPEN] UI22 evidence:** [Runs rich cell templates](../../apps/web/src/app/features/runs/list/run-list-table.component.ts) use the shared table's 200-row loaded pages; [ADR-0011, line 75](../adr/0011-spartan-tailwind-foundation.md) still requires virtualization above 200 rows. This is an owner decision, not a waiver.
