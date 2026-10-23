Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `17-studio-template-gallery/evidgate-test-studio.html`, `18-studio-draft-editor/evidgate-test-studio.html` and `19-studio-review-merge/evidgate-draft-review.html`; edit this retired page family in place.
**Phase:** G4 reference only. **Nav item:** `studio` (preserve `data-nav` on `<html>`).
**Plan ids:** 03.7.1-T1, 03.7.1-T2 (superseded), 13.3.1, 09.6.1, 13.4.1-T7.

## Goal

Make it unmistakable that the old Studio mockups are historical G4 references, and send users to the current authoring flow without redesigning these pages.

## Change

- On page 17, insert a persistent reference banner immediately before `.title-row` / `page-title` inside `test-studio-page`; do not mistake its duplicated editor content for a reason to rebuild a gallery.
- On page 18, insert the same banner immediately before `.title-row` / `page-title` inside `test-studio-page`.
- On page 19, insert it immediately before `#pageHeading` / `page-heading` inside `.content`, leaving `runtime-banner` intact.
- Banner text is "Superseded by authoring (CR-D-2), G4 reference" with a normal link to `../40-authoring-tree/evidgate-authoring-tree.html`. Reuse an existing neutral notice style and visible keyboard focus; no dismissal or mutation action.
- Keep this reference banner outside transient `#banner` / `#stateNotice` regions so scenario changes never overwrite it. Preserve it across locale/theme/loading/error/conflict fixtures.
- Rely exclusively on B1's shared nav phase gate to hide `studio` in G1–G3. Do not hand-write navigation, edit the shared nav array again or promote Studio into authoring.

## States (in addition to the shared switcher)

- Reference banner stays visible in every existing scenario and both locales/themes, including page 19's review/merge states; it must not imply those old flows shipped.
- On mobile the banner and authoring link wrap, remain readable and do not introduce a mutation control.

## Fixture (illustrative, labelled as such)

- Use the delta brief's canonical fixture table verbatim for any current authoring context; retain existing `d-114` / `case-checkout-17` only as explicitly historical Studio references. The banner introduces no new sample case, script, run, credit amount or production data.

## Copy EN / VI

- "Superseded by authoring (CR-D-2), G4 reference / Đã được thay thế bằng luồng soạn kiểm thử (CR-D-2), tài liệu tham khảo G4".
- "Open authoring / Mở phần soạn kiểm thử".

## Keep unchanged

- All existing Studio layout, headings, editor fields, dry-run/lint/AI panels, assertion diff, independent approval, PR panels, state controls and behavior. No restyle, renaming, functional migration or new token/dependency.

## Done when

- Banners are baked into static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- All three pages work at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches each new link with visible focus, and nothing overflows.
- Each persistent banner opens page 40; only banner-related markup/copy/style reuse changes, and Studio remains hidden in G1–G3 via the existing shared gate.
