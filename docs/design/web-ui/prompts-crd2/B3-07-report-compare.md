Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `07-report-comparison/evidgate-report-compare.html`; edit in place using shared `assets/`, not a separate self-contained artifact.
**Phase:** G2. **Nav item:** `runs` (preserve `data-nav` on `<html>`).
**Plan ids:** 09.2.1-T4, 09.2.1-T8, 13.4.1-T7.

## Goal

Compare source reports and normalized evidence without presenting a release verdict before G4.

## Change

- In `run-context` / `#run-context .context-fields`, remove the "Release decision · stored" field and its FAIL chip. Keep manifest, evidence revision, policy provenance, collection completeness and test outcome as separate facts.
- Adjust the `runContextTitle` explanatory copy to describe stored run context, not a release evaluation. Preserve `../04-run-detail/evidgate-run-detail.html` as the run link.
- Remove release-decision values/copy from `review-state-message`, `design-review-controls` scenarios and design notes wherever they surface in G2/G3. Do not remove original raw failure/status discrepancies from comparison rows.
- Preserve `comparison-report`, source/normalized counts and digests, `#caseRows`, discrepancy controls and mapping links. Equal row counts still do not establish equal content.
- Use the canonical fixture table for any case/script/run context being displayed; do not create new manual-case names, script states or run-source alternatives.

## States (in addition to the shared switcher)

- Existing populated/loading/empty/error/permission/conflict specimens remain available; none displays a release verdict in G2/G3.
- Incomplete collection with `has_failures` remains distinct from source-report discrepancies; missing data never implies a passed test.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `run-1842` from CI (GitHub Actions); `run-1843` local (`evidgate run`, laptop of Linh).
- If manual-case context is shown, use `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5, `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2 or `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3.

## Copy EN / VI

- "Run context / Ngữ cảnh lượt chạy"; "Collection completeness / Mức độ đầy đủ của bằng chứng"; "Test outcome / Kết quả kiểm thử".
- "Source and normalized evidence remain separate / Bằng chứng nguồn và bằng chứng chuẩn hóa vẫn được hiển thị riêng".

## Keep unchanged

- Original report comparison layout, counts/digests, raw/normalized statuses, immutable evidence revisions, discrepancy interaction, mapping links, keyboard behavior, shell and tokens. No restyle or evaluation logic change.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. Every control retains visible keyboard focus and nothing overflows.
- No G2/G3 default or scenario shows a release verdict; completeness, test outcome and source-versus-normalized comparison remain intact.
