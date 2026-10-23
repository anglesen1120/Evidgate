Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict. Follow its Deliverable and links section and use relevant Canonical fixtures verbatim.

**Page:** `23-onboarding-wizard/evidgate-onboarding.html`. Edit in place.
**Phase:** G1 (CI continuation from G2). **Nav item:** `onboarding` (set `data-nav` on `<html>`).
**Plan ids:** 13.4.1-T4, 13.4.1-T5, 13.4.1-T7, 13.2.1-T1, 13.3.1-T1, 09.2.1-T1, 09.2.1-T2, 09.2.1-T3.

## Goal

Guide a new project through AI settings, its first requirement and its first reviewed checklist, without making CI a G1 prerequisite.

## Change

- Replace `onboarding-heading` advisory-collection intro and `onboarding-stepper` with four G1 steps: project → AI settings → first requirement → first checklist. Update active-step-panel, stepKicker, progressNote and Back/Next labels to the same count.
- In `data-step-view="1"`, keep project/organization/description fields; move repository input and connection requirements to the G2 continuation. Project creation remains server-confirmed and retries reuse the same creation intent.
- Replace G1 `data-step-view` bodies 2–4 with a settings summary/edit entry to `../44-ai-settings/evidgate-ai-settings.html`, requirement intake matching `../43-generation/evidgate-generation.html`, and prioritised checklist review with a link to `../42-pending-changes/evidgate-pending-changes.html`. Include all four bodies statically, not only step 1.
- AI settings show settings version, vi/en choice and tenant opt-in status. Before checklist generation show estimate 12 credits and balance 1 240 of 2 000; do not infer opt-in from completing settings or fabricate a result.
- Rework `created-resources`/evidenceList and resumeTarget to show project, settings version, requirement and checklist version from supplied resource fixtures; resume the first incomplete step, not browser completion flags.
- Replace `onboarding-terminal` with first-checklist readiness and secondary links to `../40-authoring-tree/evidgate-authoring-tree.html` and `../43-generation/evidgate-generation.html` test-case stage; completing a request alone is not checklist approval or a saved version.
- Keep connect-repository/CI, sample run and report-comparison specimens in a separate G2+ continuation; do not let the old fifth policy-activation step block G1 completion. Hide policy controls and release verdicts at G1–G3; release configuration belongs to G4.

## States (in addition to the shared switcher)

- Resume at each G1 step; settings not saved; requirement empty; AI opt-in off; credits exhausted; gateway unavailable; checklist pending review; manual checklist path when AI cannot run. Each state retains safe entered content and shows an honest next action.
- G2 continuation: missing binding/expired token, no run yet and incomplete collection. 412 only for supported settings/review edits, never invented on project creation.

## Fixture (illustrative, labelled as such)

- Lotus Commerce / storefront-web / `SHOP` / `p-storefront`; settings `ais-3`; requirement “A customer reviews an order before confirming / Khách hàng xem lại đơn hàng trước khi xác nhận” + `checkout-requirement.pdf`; `SHOP-CL-0017` “Show the order total / Hiển thị tổng đơn hàng”, priority high; provenance models `model-small` / `model-large`. Period 2026-10 balance 1 240 of 2 000; checklist estimate 12, test cases 4 per checklist item. No invented numeric upload caps.

## Copy EN / VI

- “Create project / Tạo dự án”; “AI settings / Cài đặt AI”; “First requirement / Yêu cầu đầu tiên”; “First checklist / Danh sách kiểm tra đầu tiên”.
- “Review checklist / Xem xét danh sách kiểm tra”; “Checklist ready for review / Danh sách kiểm tra đã sẵn sàng để xem xét”; “Connect CI / Kết nối CI”.

## Keep unchanged

- Shell, stepper visual tokens, flow-footer placement, globalAlert/live status, resource-derived resume model, read-only mobile and secret-redaction rules for G2 connection specimens.

## Done when

- Changes are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- All four G1 steps and their resume states are inspectable; G1 can finish without CI or AI generation, and cannot claim a generated checklist while opt-in is off.
