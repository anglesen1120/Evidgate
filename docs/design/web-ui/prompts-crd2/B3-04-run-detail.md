Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `04-run-detail/evidgate-run-detail.html` and its same-file family `05-evidence-panel/evidgate-run-detail.html`; edit both in place, preserving their distinct evidence links.
**Phase:** G2. **Nav item:** `runs` (set `data-nav` on `<html>`).
**Plan ids:** 13.5.2-T1, 13.5.1-T4, 13.6.1-T1, 13.6.1-T2, 09.3.1-T2, 09.3.1-T3.

## Goal

Trace each result to its manual case and generated script, and see whether bound-step profiles were captured without exposing profile content.

## Change

- In `run-identity` and `run-environment`, add supplied source metadata: local `evidgate run` or CI. Both execute on customer infrastructure.
- In `attempts-panel` / `#attemptPanelBody`, add manual-case and script links per execution/result, keeping separate attempt rows and pinned case revision. Link to `../41-test-case-editor/evidgate-test-case-editor.html` and `../46-generated-scripts/evidgate-generated-scripts.html`.
- Under each result, add an ordered bound-step metadata list: step number, bound/unbound, profile captured/not captured, source green run, case revision, profile hash, spec version and capture UTC time when supplied.
- Show latest-green-profile metadata only; a failed current attempt must not masquerade as a newly captured green profile. Unbound steps say not applicable, not captured.
- In `evidence-workbench`, retain the frozen revision selector and show the same profile-capture metadata without raw role/name/id/text values, element contents, input values or expandable DOM/profile payloads.
- Remove release-verdict UI from `evaluation-fields`, `.revision-eval`, `decision-context`, `related-evaluation-variants`, `run-heading` tabs and `related-surfaces` links/copy, including review scenarios. Keep completeness and test outcome separate; remove decision-only permission copy, not authoritative stored test data.
- Preserve the original failed attempt and missing-shard warning; update any warning that conflates test FAIL with a release verdict to describe only the unchanged failed test.

## States (in addition to the shared switcher)

- Captured green-step profile; capture unavailable with supplied reason; no prior green profile; unbound step; no linked script/manual case; historical case revision.
- Pending/collecting and no frozen revision; old evidence pinned while newer evidence exists; restricted artifacts retain metadata only.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixtures verbatim: `run-1842` from CI (GitHub Actions); `run-1843` local (`evidgate run`, laptop of Linh); `gs-0042` → `SHOP-TC-0042` "Confirm order shows the total / Xác nhận đơn hàng hiển thị tổng tiền" v5 (v4 is history), active, 3/3 green on `b-318`, PR #214 merged.
- `gs-0043` → `SHOP-TC-0043` "Required delivery address / Địa chỉ giao hàng bắt buộc" v2, draft, 2 unbound steps, PR not opened; `gs-0051` → `SHOP-TC-0051` "Apply a discount code / Áp dụng mã giảm giá" v3, flaky (green on retry in 1 of 3), PR #219 open.
- For a supplied green attempt of `gs-0042`, step 2 has captured profile metadata, spec `v1`, abbreviated hash `ep-a91…74c`. The 2 unbound steps belong to draft `gs-0043`, never the active script; no real element content is included.

## Copy EN / VI

- "Profile captured / Đã ghi nhận hồ sơ phần tử"; "Last green profile / Hồ sơ từ lần chạy đạt gần nhất".
- "No green profile yet / Chưa có hồ sơ từ lần chạy đạt"; "Unbound step / Bước chưa liên kết phần tử".
- "Manual test case / Ca kiểm thử thủ công"; "Generated script / Kịch bản được tạo"; "Metadata only / Chỉ hiển thị siêu dữ liệu".

## Keep unchanged

- MissingReasonsList, artifact availability restrictions, original/raw and normalized attempt statuses, retry distinction, evidence hashes, UTC timeline, shell and tokens. Do not alter page 06 or the artifact viewer.

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- Both family pages work at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- Every result exposes the supplied case/script links and per-step capture status; neither family page exposes profile content or release verdicts in G2/G3.
