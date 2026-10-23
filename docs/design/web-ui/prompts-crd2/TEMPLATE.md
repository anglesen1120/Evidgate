# Prompt template (CR-D-2 delta prompts)

Copy everything between the two rules below into `start_run.prompt`. Pass `skill: "frontend-design"`. Keep each prompt at 60 lines or fewer.

---

Use the `frontend-design` skill.

Project: Evidgate — Web UI. Before changing anything, read `00-shared-brief.md` and `crd2-delta-brief.md` in the project root. They are binding, and the delta brief wins on conflict.

**Page:** `<NN-folder>/<file>.html`. Choose one: edit in place, or create a new page that reuses the shell and `assets/` exactly like `03-run-list/evidgate-runs.html`.
**Phase:** G<n>. **Nav item:** `<item id>` (set `data-nav` on `<html>`).
**Plan ids:** <13.x.y-Tn, 09.x.y-Tn>.

## Goal

<One or two sentences: what the user can do on this page after the change.>

## Change

- <Concrete layout or behaviour change. At most 12 bullets. Name the region of the existing page.>

## States (in addition to the shared switcher)

- <Page-specific states, for example AI opt-in off, credits exhausted, 412 on apply, unbound steps.>

## Fixture (illustrative, labelled as such)

- <ids, names, counts. vi and en values where the text is user content.>

## Copy EN / VI

- <Key strings as "English / Tiếng Việt".>

## Keep unchanged

- <Regions, tokens and behaviours that must not be restyled.>

## Done when

- Changes are baked into the static HTML, not injected by page-specific JS. The sidebar still comes from the nav array in `assets/evidgate.js`.
- The page works at 1280 px and 360 px, in light and dark, in vi and en. The keyboard reaches every new control with visible focus, and nothing overflows.
- <One or two page-specific checks.>

---
