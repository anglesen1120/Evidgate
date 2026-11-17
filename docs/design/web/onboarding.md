# Onboarding

Flow id: `onboarding`

## Current reference and owner decision

Owner direction, 2026-11-17: keep the **five existing steps**, redesign the shell from UX review and pattern research, and update this reference to match. The approved design summary is one top navigator, a full-width body and independent completion/viewing signals. This is approval to implement the design, not a claim of completed QA or feature acceptance.

The [primary HTML][primary] now references the current API-backed setup flow instead of the older four-step authoring proposal. [UI53/UI62][rules] record the exception. The [feature contract][feature] remains authoritative for ordering, server-derived progress, permissions, API-token scope, one-time reveal and advisory activation. No product phase, API or authoring-plan contract changes here.

The former four-step G1 authoring mockup, a duplicate vertical tablist/select and a separate CI continuation card are not copied into this shell. A dedicated authoring-onboarding flow would need a separate owner decision; this redesign does not implement it or change the phase map.

## Goal and information hierarchy

Help a returning project owner see what the service has confirmed, identify the next required action and revisit an available step without marking anything complete.

1. Page title **Set up your project** and the explanation that viewing does not change progress.
2. **Setup progress:** one interactive ordered navigator, confirmed completion count and progress bar.
3. Full-width body headed by the viewed step's actual name; no generic “Step details” heading or second list.
4. Existing body actions, followed by **Back → Next** with the same prerequisite/terminal reasons.
5. **Created resources:** secondary, server-confirmed facts; explicit empty, unavailable and stale states remain distinct.

The app reuses Helm button/card/icon primitives on the existing tokens, installed Angular CDK focus management and the existing onboarding store. There is no new styled component library, stepper store or progress flag.

## Five-step contract

| Order | English | Vietnamese | Completion source |
|---|---|---|---|
| 1 | Create project | Tạo dự án | Confirmed project resource |
| 2 | Connect CI | Kết nối CI | Existing usable connection/token condition |
| 3 | Sample run | Lượt chạy mẫu | Confirmed sample-run condition |
| 4 | Compare reports | So sánh báo cáo | Confirmed evidence-revision condition |
| 5 | Enable collection | Bật thu thập dữ liệu | Confirmed activation read |

The [shell source][shell-source] keeps the current order and delegates selection to the existing store. Resources, not clicks, determine `done`, the next required step, completion count, Back/Next gating and the resumable project address. Existing pending sample-run/comparison bodies remain honest pending surfaces; a completed resource flag does not claim a new workflow was implemented by this redesign.

### Two independent signals

- **Completion:** check-circle plus “Complete”; clock plus “Next required”; open circle plus “Upcoming”. All have visible localized text; color is not the sole signal.
- **Viewed:** exactly one action has `aria-current="step"`, a selected border/background and visible **Viewing / Đang xem** text. A completed step can be viewed while another step remains required.
- Number markers and repeated status icons are decorative. The action's full authored label remains available; labels are not shortened, ellipsized or relegated to a tooltip.
- Future steps use `aria-disabled="true"`, stay keyboard-focusable and reference the visible prerequisite through `aria-describedby`. The click handler checks availability again.

## Interaction and accessibility

- Native **Tab** reaches each of the five step buttons. **Left/Right/Up/Down**, **Home** and **End** move focus only; they never select, complete or fetch a step. **Enter/Space** activates an available button through its native click behavior.
- Activating a different available step preserves the existing heading-focus/announcement handoff. A blocked activation leaves both the viewed step and focus unchanged.
- Five focusable step actions deliberately replace the former single enabled action at empty setup: **4 → 8** main-region Tab stops (five steps, two existing fields, one existing submit). This exposes each blocked label/reason without relying on knowledge of an optional arrow shortcut; no form/copy Tab stop was added.
- Back/Next order, disabled reasons, create-to-project handoff, route reload/resume and terminal focus remain on the existing contract.
- Mutations retain the narrow read-only policy. Inspecting a step is not a mutation; policy preview and its desktop-required explanation remain reachable on narrow screens.
- One-time values remain confined to the shared reveal boundary and clear through the existing lifecycle. No token scope, reveal component, confirmation dialog or network contract changes in this shell redesign.
- Existing focus tokens, coarse-pointer targets, dark/light colors and reduced-motion behavior are reused. Status text is ordinary wrapping progress metadata, not a compact single-line domain badge.

## Responsive layout

| Viewport | Navigator | Body and resources |
|---|---|---|
| 390px (also 320px reflow) | Same five controls in one vertical column; no horizontal carousel, duplicate select or abbreviated labels | Full-width stacked regions; existing read-only mutation policy |
| 768 / 1024px | Five equal-width tiles in one horizontal row; labels/status text wrap naturally | Full-width body, no navigation rail beside it |
| 1280 / 1440px | Same horizontal navigator; existing expanded app navigation is unchanged | Body uses the available content width; ledger remains separate below |

Five Vietnamese labels cannot share a 390px horizontal row without narrow targets, truncation or horizontal scrolling. Stacking the **same** controls below the existing 768px breakpoint preserves their full names, order and minimum 44×44px targets. Narrow tiles use 8px padding and gaps, 14/20 labels and 12/18 metadata; from 768px they use 12px padding and start-aligned grid content so label first lines align. Desktop rows allow wrapping rather than imposing one-line labels. The app adds no animation or new motion duration.

## Reference assets and specimens

- [Primary HTML][primary] retains the shared shell, theme/locale review controls, state styles and shared assets.
- [Page-local CSS][local-css] describes only the new navigator/body/ledger layout using existing `--eg-*` tokens.
- [Page-local JS][local-js] is static-reference interaction, not production code. It separates immutable illustrative resource progress from the viewed step, uses text APIs rather than `innerHTML`, and sends no API request or secret.

Open the HTML over the existing static-site server. `?setup=empty|project|connected|run|evidence|enabled` chooses **0–5 confirmed illustrative steps**; the default is `project` (step 1 complete, step 2 required/viewed). Use `setup=evidence` to inspect all five available bodies; `setup=enabled` shows the terminal specimen. Choosing a step never changes that specimen's completion count.

### States

Review-only `?state=default|loading|error|403|404|401|429|412` retains the existing shared state surfaces. Empty setup is a resource specimen (`setup=empty`), not a generic entry-state override: this avoids the shared reference script's unrelated “New manual case” rewrite for `state=empty`. Static error examples do not prove typed HTTP recovery, draft restoration or live permissions.

The body is a layout reference: it shows the existing connection groups and neutral pending/preview/terminal regions, not a substitute CLI recipe or interactive credential API. It never fabricates a one-time value. The app's real fixtures and shared reveal specs remain the behavioral evidence.

The older images under `images/onboarding/` depict the superseded four-step mockup and are historical, **not current design or acceptance evidence**. Current visual evidence is captured from the implemented fixtures build at 390/768/1280/1440px, in Vietnamese light and dark, for the review packet.

## Prior art

- [Angular CDK stepper][cdk]: separate selection from completion and use framework keyboard/focus behavior. Do not import Material's styled stepper, copy a different version's implementation or equate selection with progress.
- [Carbon progress indicator][carbon]: ordered milestones, whole-label interaction and explicit status. Do not copy disabled-unfocusable steps or label ellipsis: this page requires discoverable reasons and complete Vietnamese names.
- [Atlassian progress tracker][atlassian]: five stages fit a short ordered flow with revisits. Do not equate visited with complete or force one-line short labels.

A setup checklist was rejected because these are ordered, gated milestones rather than independent tasks. One responsive navigator communicates order and revisitability without maintaining a second navigation surface.

## Runnable coverage and remaining proposals

- [Unit navigator specs][unit-spec]: one ordered control set, independent viewed/completed signals, guarded focusable future steps, arrow/Home/End behavior, completed setup and loading/remount.
- [Browser navigator specs][browser-spec]: en/vi × light/dark × 390/768/1024/1280/1440px, geometry/overflow/full labels, axe contrast, focus/activation independence and Vietnamese 320px text/spacing expansion. Zero-complete and all-five-available fixtures also cover 320/767/1279px boundaries and first↔last revisits without changing confirmed progress.
- Existing onboarding keyboard, refusal, resume, secret-boundary and core-flow suites keep their behavioral assertions while following the current action marker.
- No shared gallery currently represents this step-navigation pattern; no unrelated gallery specimen is changed.
- **Proposal:** expose a supported focus-target API on shared PageHeader before replacing the existing shell heading; this task keeps the established reload-failure focus target rather than querying/mutating a shared component's private DOM.
- **Proposal:** decide a separate G1 authoring-onboarding flow through its own feature contract; do not silently merge or remove any of these five steps.

[primary]: ../web-ui/output/23-onboarding-wizard/evidgate-onboarding.html
[local-css]: ../web-ui/output/23-onboarding-wizard/onboarding.css
[local-js]: ../web-ui/output/23-onboarding-wizard/onboarding.js
[rules]: ../../engineering/ui-rules.md
[feature]: ../../../plans/features/09-web-ui/09.2.1-onboarding-wizard-acceptance-a-new-tenant.md
[shell-source]: ../../../apps/web/src/app/features/onboarding/shell/onboarding-shell.component.ts
[unit-spec]: ../../../apps/web/src/app/features/onboarding/shell/onboarding-shell-stepper.spec.ts
[browser-spec]: ../../../apps/web/e2e/shell/onboarding-navigator.spec.ts
[cdk]: https://github.com/angular/components/blob/main/src/cdk/stepper/stepper.ts
[carbon]: https://www.carbondesignsystem.com/building-blocks/core/components/progress-indicator/guidelines
[atlassian]: https://atlassian.design/components/progress-tracker/usage
