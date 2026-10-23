Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `31-quota-retention/evidgate-usage-retention.html`. Edit in place.
**Phase:** G1 (existing quota/retention from G2). **Nav item:** `quota` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T7, 08.5.5-T2, 09.7.1-T5.

## Goal

Let a team inspect its AI credit balance and usage by action and period without confusing credits, artifact quotas or release outcomes.

## Change

- Rename `.title-row`/page-title to usage and AI credits; retain `.scope-line` with explicit organization balance scope and selected-project usage context, plus supplied as-of timestamp. Never present a tenant balance as a project allocation.
- Insert an AI credit balance section before `usage-metrics`; show remaining credits, supplied status and timestamp. Unknown balance is unavailable, not zero; make no currency conversion, pricing promise, checkout or top-up flow.
- Extend `.section-head`/`#usageWindow` with a clearly labelled period for AI usage and explicit UTC bounds. Place a dense action table beneath balance: action, credits consumed and period; include invocation counts only if supplied, never derive them from estimates or calculate balance from the visible rows.
- Actions: checklist, test case, translate at G1; script mapping at G2; locate fallback at G3. Keep the same labels as generation preflight; later-phase actions are hidden, not disabled previews.
- Keep `usage-metrics`, `retention-settings` and quota source records as separate G2+ regions. Preserve their supplied warned_at/blocked_at semantics, null egress and deletion-impact safeguards; do not repurpose storage warning `#quotaAlert` as a credit verdict.
- Add credit-specific review scenarios to `design-review-controls`: exhausted balance, no action records, missing balance, missing action counter and action-table fetch error; retain period selection and any available independent summary.
- Keep `.records .contract-note` clear that this is usage only. Canonical credit estimates are illustrative, not approved tariffs or money prices; model ids `model-small` / `model-large` are provenance placeholders, not a model-selection feature.

## States (in addition to the shared switcher)

- Zero confirmed credits versus unavailable balance; no usage versus no matches for period; opt-in off; stale/unavailable usage; restricted usage permission; exhausted credits with manual authoring still available. G2 retention 412 stays an edit specimen, not a usage read response.

## Fixture (illustrative, labelled as such)

- Lotus Commerce / storefront-web / `SHOP` / `ais-3`; period 2026-10, UTC 2026-10-01T00:00:00Z–2026-11-01T00:00:00Z; balance 1 240 of 2 000 credits. Estimates: checklist generation 12 · test cases 4 per checklist item · translate 1 per case · script step mapping 2 per unbound step · locate fallback 3 per incident. Usage this period: checklist 312 · test cases 284 · translate 41 · script mapping 96 · locate fallback 27. Use exact canonical numbers; show the applicable rows by phase, never money. Provenance model ids: `model-small` / `model-large`.

## Copy EN / VI

- “Usage and AI credits / Mức sử dụng và tín dụng AI”; “AI credit balance / Số dư tín dụng AI”; “Usage by action / Mức sử dụng theo thao tác”; “Usage period / Kỳ sử dụng”.
- “Credits exhausted / Đã hết tín dụng AI”; “Balance unavailable / Không có dữ liệu số dư tín dụng AI”; “Checklist / Danh sách kiểm tra”; “Test case / Ca kiểm thử”; “Translate / Dịch”; “Script mapping / Ánh xạ kịch bản”; “Locate fallback / Định vị dự phòng”.

## Keep unchanged

- Topbar scope, refresh feedback, shell permissions and theme/locale, G2 storage/ingest/egress units, retention form, `impactDialog` confirmation flow and read-only mobile notice.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Balance and action rows have unambiguous scope/period; no money amounts, invented credit prices, false zeroes or G2/G3 action teasers appear at G1.
