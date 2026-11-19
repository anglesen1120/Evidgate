Status: Approved 2026-11-19 by the coordinator on the owner's delegation (owner may override); [OPEN] defaults accepted as recorded in docs/status-carried.md

# Authoring usage credits

Review record: [authoring-review.md](authoring-review.md)

Flow id: `authoring-usage-credits`. CR-D-2 phase: **G1** credit region; quota/retention remains **G2**.

Feature tasks: `13.4.1-T1.1`, `13.4.1-T1.2`, `13.4.1-T7.2`; contract owner `08.5.5-T5.2` for `OI-13-504`.

Legend: ✅ source-verified with a link · ⚠️ not exercised · `[INFERENCE]` interpretation · `[DECISION]` recorded decision · `[OPEN]` unresolved, with recommended default.

✅ Sources: [authoring plan §5–6 and T7](../../../plans/features/13-product-phases/13.4.1-authoring-ui.md#5-api-shapes-commands-queries-endpoints), [B1-04](../web-ui/prompts-crd2/B1-04-usage-credits.md), [delta brief](../web-ui/prompts-crd2/00-delta-brief.md), [shared conventions](state-sheet.md), [layout](layout.md), [tokens](tokens.md), and [A15 catalogue](../../engineering/ui-rules.md#shared-component-catalogue-a15). This is a proposed design specification, not evidence of browser, API or accessibility acceptance. Independent design review and owner approval remain outstanding.

## v2 pages

| Page | Role | Declared `?state=` values |
| --- | --- | --- |
| [31 · Usage and retention](../web-ui/output/31-quota-retention/evidgate-usage-retention.html) | Primary credit region, `data-nav="quota"`; credit region before G2 resource usage and retention | `default`, `loading`, `empty`, `no-match`, `table-error`, `error`, `403`, `404`, `401`, `429`, `412` (HTML:78) |
| [01 · Workspace shell](../web-ui/output/01-app-shell/evidgate-workspace-shell.html) | Navigation context only; its credit specimens are not another live balance surface | See [nav design](authoring-nav-phase.md) |

The application destination is **`/orgs/:orgId/usage`**, sidebar id `quota`, org scope, segment `usage`, permission `usage:read` ([registry](../../../apps/web/src/app/shell/nav/nav-registry.ts), `quota` row). It is not the project Settings `quota-retention` tab. That tab remains a placeholder and contains no balance. Static review switches do not grant access.

The plan names `GET /v1/orgs/{o}/ai-credit`, but the current [OpenAPI](../../../contracts/openapi.yaml) has no AI-credit operation or response schema. `UsageSummaryDto` describes resource usage, not credit usage: do not reinterpret its metrics as credits. Proposed member names below are **not** schema citations until the owning contract lands.

| Data field | openapi schema path | state (traced / [OPEN]) |
| --- | --- | --- |
| Organization identity and name | `contracts/openapi.yaml#/components/schemas/OrganizationDto.id`; `contracts/openapi.yaml#/components/schemas/OrganizationDto.name` | traced; show only authorized scope context |
| Selected project identity, organization and name | `contracts/openapi.yaml#/components/schemas/ProjectDto.id`; `contracts/openapi.yaml#/components/schemas/ProjectDto.org_id`; `contracts/openapi.yaml#/components/schemas/ProjectDto.name` | traced; context is not an allocation |
| Remaining balance | No schema; proposed `balance_micro` | [OPEN] `OI-13-504`: nullable exact micro-credit amount; unavailable with reason when absent |
| Period cap | No schema; proposed `period_cap_micro` | [OPEN] `OI-13-504`: nullable server cap; absent cap is unavailable, not zero or unlimited |
| Selected period, UTC start/end, available period options | No AI-credit schema or period-filter contract | [OPEN] `OI-13-504`: server period `{start,end}` and supported options; recommend explicit start-inclusive/end-exclusive bounds, subject to owner confirmation; no invented historical request |
| Balance/data as-of | No schema; proposed `as_of` | [OPEN] `OI-13-504`: one supplied UTC timestamp with clear scope; never browser time |
| Action label and consumed credits | No schema; proposed `usage[].action`, `usage[].credits_micro` | [OPEN] `OI-13-504`: stable server action enum plus nullable exact credits; render known G1 actions only |
| Invocation count | No schema; proposed optional `usage[].calls` | [OPEN] `OI-13-504`: show only when supplied; missing is unavailable, not zero or estimate-derived |
| Balance/usage unavailable reason and service status | No schema; proposed `unavailable_reason` | [OPEN] `OI-13-504`: explicit reason and scope; no invented “Available” success badge when unknown |
| Project-specific action rows/filter | No schema; mockup `#usage-project` | [OPEN] recommend org action rows with selected project labelled context-only until the API explicitly supports project filtering; never locally filter an incomplete response |
| “Recorded / Period total”, source ids and per-record supplied timestamp | No AI-credit schema; HTML:192–225,323–325 | [OPEN] omit source/record-time columns unless returned; use the supplied period as caption, not invented ledger records |
| Checklist unit estimate “12”, per-item prices, model ids | No schema for this usage read; illustrative brief only | [OPEN] omit estimates, tariff claims and model selectors from the live usage page; generation preflight owns its actual total estimate |
| Balance meter `62%` | No schema; hard-coded HTML:123 | [OPEN] omit the decorative percentage meter; display exact supplied balance/cap text, never generation progress |
| Tenant AI opt-in off/on | No estimate schema in current OpenAPI; plan fixes source as generation estimate `opt_in` | [OPEN] show known opt-in state only from an already available valid estimate; absent estimate means status unavailable, never a second opt-in request or guessed consent |
| Sidebar grants and phase | Not a credit DTO field; existing permission source and build configuration | [OPEN] no new schema proposed here; retain existing shell authorization and build `ACTIVE_PHASE`, never role-name inference |

## Regions

Reading order follows page 31 HTML:49–231, with the following application constraints:

1. Skip link, shell navigation and topbar; organization scope is primary. Breadcrumbs must not imply a project-owned balance.
2. One page heading, “Usage and AI credits”, organization name, optional selected-project context, and supplied as-of. Refresh feedback belongs beside the affected read, not a money or release-status chip.
3. A route-level request/forbidden/not-found slot, followed by the org credit summary when readable. Summary facts use semantic `dl/dt/dd`: balance, cap, UTC period and as-of. Confirmed zero and unavailable are mutually exclusive value states, not simultaneous live KPI cards.
4. An opt-in explanation only when its authoritative estimate state is available. Off explains that AI generation cannot run; manual requirements, checklists, cases and review remain available under their own grants. No consent toggle, checkout or fabricated administrator URL.
5. Usage-by-action section: supported period control before a captioned table; keep the period/reset control reachable during empty, no-match and error states. Columns: action, consumed credits, optional supplied calls; caption contains UTC period. No invented source-record identifiers. At this design scope show checklist, test case and translate only; script mapping and locate fallback are later-phase data, not disabled teasers.
6. Inline table-only loading/error/empty feedback and Retry. Retain independently successful summary data with its actual as-of. Do not create a second HTTP endpoint to imitate independently loaded mockup sections.
7. G2 resource quotas, storage/ingest/egress and retention remain separate and hidden at G1. The Settings `quota-retention` destination is still a placeholder. Preserve their deletion-impact safeguards when their owning feature ships; no storage warning becomes a credit warning.

Reused A15 components: `app-page-header`, `app-section-header`, `app-request-state`, `app-inline-feedback`, `app-retry-after`, `app-number-value`, `app-data-table`, `app-data-table-records`, `app-data-table-value`, `app-desktop-required-notice`, and `app-absolute-time`; owned Helm select/button/card/table/alert primitives where appropriate. NEW names only: `shared/ui/authoring/credit-summary`, `shared/ui/authoring/credit-usage-table`. They are presentational inputs/events, never SDK clients or feature stores; the later handover owns component/id mappings.

## States

The plan’s Usage credits row contains **8 atomic states**, splitting each “vs” into both distinct outcomes. All eight are specified below. HTTP/request extensions follow the shared §6 state model, not additional credit lifecycle values.

| State | Region behaviour and recovery | Mockup `?state=` value or gap |
| --- | --- | --- |
| Balance present | Render exact supplied balance/cap and timestamp in org summary; usage remains independent | `default` |
| Zero balance, confirmed | Show `0` only from a successful numeric balance; explain exhausted AI credits, leave manual authoring available | [OPEN] static “Confirmed zero” KPI at HTML:126, no dedicated query state |
| Unavailable balance | Replace balance with localized unavailable text and reason; never `0`; omit meter and cap-derived claims | [OPEN] static unavailable KPI at HTML:127, no dedicated query state |
| No usage | Successful empty collection, neutral “No usage in this period”; retain independently readable balance | `empty` |
| No matches for selected period | Preserve period/context and show reset; do not imply organization-wide absence | `no-match` |
| Opt-in off | Explain no AI call, keep usage history and manual authoring inspectable; only estimate `opt_in` is authoritative | [OPEN] no dedicated specimen/state |
| No `usage:read`: sidebar | Hide `quota` and every link disclosing credit data; no balance request or partial region | [OPEN] permission-filtered nav is not a page query state |
| No `usage:read`: typed URL | Land on `/forbidden`; replace the whole protected page, not just the table; no summary or partial page | `403` visual pattern only; static fixture does not demonstrate route guard |
| Idle / initial loading | No fabricated numbers; mark affected region busy, hide decorative skeletons from AT; announce “Loading AI credit usage” once | `loading` only demonstrates table load with a static summary; [OPEN] initial summary load missing |
| Table read failure | Persistent inline error and Retry; preserve successful summary only if real response/state permits it; retain period/context | `table-error`, `error` |
| Stale after failed refresh / unavailable | Keep last successful values explicitly stale with original as-of; show failure reason, never claim current balance | [OPEN] no named stale specimen |
| 429 / 503 | Typed error and supplied `Retry-After`; disable Retry while waiting/in flight; no fake countdown or automatic mutation | `429`; [OPEN] distinct `503` specimen absent |
| 401 / 404 | Sign-in with safe return URL; unknown/foreign organization uses not-found without existence disclosure | `401`, `404` |
| 412 | Not a credit-read state; this mockup is a linked G2 retention edit specimen only | `412`; no usage-save or usage conflict invented |

Focus order: skip link → shell controls → page heading on route entry → refresh → supported period/context controls → reset or Retry when present → table disclosures/links in document order. Numeric cells are not gratuitous tab stops. A single polite live region outside the table announces meaningful load/filter/error transitions, not every value or background refresh; `aria-busy` is scoped to the affected section. Retry is disabled only while genuinely unavailable/in flight, with an adjacent reason. Background updates never steal focus. On permission revocation remove protected content and focus the forbidden heading; do not retain a stale balance.

## Copy length (vi vs en)

Proposed copy specimens, not measured browser widths. The longest descriptive pair is intentionally kept outside compact chips; allow natural wrapping and at least 40% expansion. Namespace: `@@usage.credits.*`; preserve raw server action/reason codes as text beside localized explanations when needed.

| Purpose | vi | en |
| --- | --- | --- |
| Heading | Mức sử dụng và tín dụng AI | Usage and AI credits |
| Scope | Số dư thuộc tổ chức; dự án đã chọn chỉ là ngữ cảnh sử dụng, không phải phân bổ tín dụng. | The balance belongs to the organization; the selected project is usage context, not a credit allocation. |
| Balance unavailable | Không có dữ liệu số dư tín dụng AI | Balance unavailable |
| Confirmed zero | Đã hết tín dụng AI; vẫn có thể soạn kiểm thử thủ công. | Credits exhausted; manual authoring is still available. |
| Empty | Không có mức sử dụng trong kỳ này | No usage in this period |
| No match | Không có mức sử dụng khớp kỳ đã chọn | No usage matches the selected period |
| Opt-in off | Chưa bật tạo bằng AI; vẫn có thể soạn kiểm thử thủ công. | AI generation is off; manual authoring is available. |
| Stale / longest recovery specimen | Không thể làm mới mức sử dụng. Dữ liệu trước đó vẫn hiển thị với mốc thời gian do máy chủ cung cấp; số dư không được tính lại từ các dòng này. | Usage could not be refreshed. Earlier data remains visible with its server-supplied timestamp; the balance is not recalculated from these rows. |
| Table failure / Retry | Không thể tải bản ghi sử dụng / Thử lại bảng | Usage records could not be loaded / Retry table |
| Period / reset | Kỳ sử dụng (UTC) / Đặt lại kỳ | Usage period (UTC) / Reset period |
| Action labels | Danh sách kiểm tra / Ca kiểm thử / Dịch | Checklist / Test case / Translate |
| Columns | Thao tác / Tín dụng đã dùng / Số lần gọi | Action / Credits consumed / Calls |
| Time / missing calls | Tính đến / Không có dữ liệu số lần gọi | As of / Call count unavailable |
| Read denial | Bạn không có quyền xem mức sử dụng của tổ chức này. | You do not have permission to read this organization’s usage. |

The canonical `1 240 / 2 000`, October 2026 and action totals `312 / 284 / 41` are labelled illustrative reference data, never live fallbacks, approved tariffs or inferred balance arithmetic. Full UTC bounds are visible without relying on hover. Show localized absolute timestamps with four-digit years and retain the raw supplied RFC3339 value via the shared time component.

## Responsive notes

- **1280 px:** expanded 224 px shell; summary → controls → table stays a vertical reading stack, with no new side rail. Magnitudes and their headers align end using tabular numerals; action text flexes. Ordinary three-action table needs no virtualization or sticky header.
- **768–1279 px:** 68 px shell rail by default; named links retain accessible labels. Stack summary facts as needed without removing period or reason text.
- **360 px:** modal navigation drawer; single-column facts and semantic labelled usage records; 12 px narrow gutters, natural vi/en wrapping, no page-level horizontal scroll or clipping to hide overflow. Read-only means no authoring edit/apply or retention save, not disabled navigation, period selection, reset, refresh or read-data disclosure. Future mutation handlers re-check `NarrowMutationPolicy`; this region itself has no mutation.
- Drawer opens with focus inside, contains Tab, makes background inert, closes on Escape and returns focus to the toggle. Resize cannot leave focus in an invisible drawer/control. Preserve logical focus order and unclipped 2 px focus ring/2 px offset; scroll targets clear sticky topbar and feedback.

| Manual WCAG 2.2 check | Required run and expected result | Evidence status |
| --- | --- | --- |
| 2.5.8 target size | Usage, 1280/360 px × light/dark × vi/en: every control ≥24×24 px; coarse pointer targets 44×44 px | ⚠️ not run |
| 2.4.11 focus not obscured | Same matrix: period/reset/Retry and drawer controls never hidden under sticky chrome, table or toasts | ⚠️ not run |
| 2.5.7 dragging | Same matrix: all inspection/navigation possible with buttons/keyboard, no drag requirement | ⚠️ not run |
| 1.4.3, 1.4.11, 2.4.7 contrast/focus | Same matrix: normal text ≥4.5:1, necessary boundaries/focus ≥3:1, visible focus in both themes | ⚠️ token recipes referenced, rendered combinations not run |
| 1.3.1, 4.1.3, 1.4.10 semantics/status/reflow | Same matrix: labelled facts/records and one polite transition announcement; 360 px without page overflow | ⚠️ not run |

## Invariants

- Null, absent, stale or failed-to-read balance/cost/count is not zero. Real zero stays numeric and receives its reason. Missing cap is not unlimited. Never sum visible rows to compute balance or subtract usage from a cap.
- Credits are not currency, storage quotas, a quality score or a release verdict. No money, checkout/top-up, model picker, guessed tariff, or PASS/FAIL credit chip.
- An org balance is shown once, on the org usage page behind `usage:read`. Context/project and period changes cannot turn it into a project allocation. No `usage:read` means no entry, protected data request, partial page or cached balance.
- Opt-in comes only from the estimate’s `opt_in`; do not call another route to deduce it, infer it from saved settings, or submit a generation merely to learn consent. Missing estimate means unavailable status.
- The plan’s G1 scope includes only checklist/test-case/translate action rows. Later-phase action amounts and G2 retention controls are absent, not teaser-disabled. Period filtering must use supported server semantics, never local filtering over an incomplete response.
- This is read-only data: no `If-Match`, `Idempotency-Key`, credit save or fabricated 412. Linked edits elsewhere use their own authoritative tags: case ETag, batch ETag, settings version, generation accept version. Missing mutation tag is server 422, stale tag 412, not a new 428 invented here.
- No generation progress percentage, ETA or per-item price is derived from credit data. Render untrusted scope names/reasons/action codes as escaped text. No HTML insertion or client clock as provenance.

## Open points

| Gap | Recommended default / receiving decision |
| --- | --- |
| [OPEN] `OI-13-504`: no credit route/schema in OpenAPI, including usage items, period, as-of and availability | Owning credit contract supplies `{balance_micro,period_cap_micro,period:{start,end},as_of,usage:[{action,credits_micro,calls?}],unavailable_reason}` with null-not-zero. Until then unavailable states, not fabricated DTOs or values. Micro-credit display precision must be specified by that contract, not rounded locally. |
| [OPEN] Project filtering, period options and empty/no-match distinction lack wire semantics | Keep project as context-only; show server-supported periods only and retain controls during results failure. Do not claim historical filtering, separate totals or no-matches from an unsupported query. |
| [OPEN] Mockup summary contains simultaneous positive/zero/unavailable values, an “Available” badge and a hard-coded meter | Treat as separate review specimens. Live state is mutually exclusive; omit meter/status inference and expose the applicable exact value/reason only. |
| [OPEN] Mockup page-header timestamp differs from summary `as_of` (HTML:71 vs 130) | Use one authoritative timestamp per explicitly named data scope; no contradictory current-as-of labels. |
| [OPEN] Mockup source ids, recorded times, unit estimates and model placeholders have no credit schema | Omit these live fields unless the owning contract supplies them; no price-per-item or provenance invented from generation fixtures. |
| [OPEN] Opt-in off, initial summary load, stale refresh and separate balance-missing query specimens absent | Add these to the later approved state capture set; keep proposed states explicit here without inventing `?state=` names. |
| [OPEN] Independent summary/table failure versus one planned credit read | Preserve only actual successfully read data and mark stale on refresh failure; let the owner define partial-response semantics, not a second UI-created endpoint. |
| [OPEN] Review and approval | Independent observations and docs-only dispositions are recorded in [authoring-review.md](authoring-review.md). Complete reference/runtime and visual/AT evidence, then owner approval, remain open; proposed status authorizes no implementation start. No new checks were run in this correction assignment. |
