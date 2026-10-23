Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `39-locale-layout-review/evidgate-localization-review.html`; edit in place.
**Phase:** G3 design-review artifact covering G1–G3 copy. **Nav item:** none; preserve the standalone review page without a production sidebar or route.
**Plan ids:** 09.8.1-T4, 13.4.1-T1, 13.4.1-T7, 13.5.1-T7, 13.8.2-T2.

## Goal

Stress the new authoring, credit, script and drift copy in realistic controls using the longest Vietnamese strings, not a disconnected translation glossary.

## Change

- Extend `translation-review` / `#copyGrid` with a CR-D-2 group; update its hard-coded twenty-string heading/count to match the expanded set without deleting existing unrelated specimens.
- Add authoring specimens for manual-case title/version, pending-change action/conflict and AI provenance; include long requirement and case names with correct Vietnamese diacritics.
- Add a credit-balance/estimated-cost region with insufficient-credit and opt-in-off specimens; credit counts are labelled illustrative, no currency, price per action or invented conversion.
- Add script status chips, same-build 3/3 stability copy and an unbound-step reason in a table/detail specimen; keep draft/active/flaky distinct from test outcome.
- Add drift-class filter, classification-reason paragraph and proposal-only status specimens. Non-test-drift labels show no repair; UI change routes toward authoring review, not automatic script repair.
- Extend `#componentControl` and `active-specimen` selection to expose the new groups; preserve locale/theme/viewport/role/state review controls and clarify that simulated roles change no real privileges.
- Bake new specimen structure and bilingual content into static HTML with local toggles; do not add another page-specific JS template renderer for the new group. Retain existing unrelated renderer behavior.
- Remove release-verdict specimens/copy from the G1–G3 review context, including existing run-detail examples; do not modify the actual G4 release pages. Keep original test outcome and completeness separate.

## States (in addition to the shared switcher)

- Long Vietnamese wrapping at 320, 360 and 1280 px; focused/pressed/disabled/loading controls; insufficient credits; AI opt-in off; pending-change 412; unbound script step; no repair; proposal awaiting review.
- No clipped labels, pills or dialog focus; desktop-required mutations disappear on mobile while design-only toolbar controls remain operable.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5; `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2; `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3. Stress long explanatory copy, not renamed canonical titles.
- Period 2026-10: 1 240 of 2 000 credits; estimated script step mapping 2 per unbound step. Use the brief's exact remaining credit/action/script/incident values wherever shown; unavailable is not zero. No real user content or claimed accessibility certification.

## Copy EN / VI

- "Apply pending changes as one version / Áp dụng thay đổi chờ áp dụng thành một phiên bản duy nhất".
- "Estimated credit cost before generation / Lượng tín dụng AI dự kiến cần dùng trước khi tạo nội dung"; "Not enough AI credits for this action / Không đủ tín dụng AI để thực hiện thao tác này".
- "AI generation is disabled for this tenant / Chức năng tạo nội dung bằng AI chưa được bật cho tổ chức này".
- "3/3 green on one build / 3/3 lượt chạy đạt trên cùng một bản dựng"; "Unbound steps keep this script in draft / Các bước chưa liên kết phần tử khiến kịch bản này vẫn ở trạng thái bản nháp".
- "UI change requires test case review, not script repair / Thay đổi giao diện cần được xem xét lại trong ca kiểm thử, không phải tự động sửa kịch bản".
- "No repair for a product defect / Không đề xuất sửa kiểm thử khi sản phẩm có lỗi"; "Screenshots are not enabled for this project / Dự án này chưa bật ảnh chụp màn hình".

## Keep unchanged

- Standalone review toolbar and simulated-role disclaimer, existing login/project-switch/artifact/settings specimens, theme tokens, safe artifact behavior and all production navigation outside this review artifact.

## Done when

- New specimen structure/content is baked into static HTML, not injected by page-specific JS; no production sidebar is added to this standalone review artifact.
- The page works at 1280 px and 360 px, in light and dark, in vi and en, plus its existing 320 px stress viewport. Every new control has visible keyboard focus and nothing overflows.
- All four new copy groups are reachable in real UI contexts, retain complete EN/VI equivalents and show no G1–G3 release verdict or invented credit price.
