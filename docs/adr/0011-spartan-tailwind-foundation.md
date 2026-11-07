# ADR-0011 — Spartan Brain, locally owned Helm and Tailwind v4

Legend: ✅ verified with link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]`.

## Status

accepted

## Date

2026-11-05

## Context

The web application uses Angular 22.2.3, CDK 22.2.4, standalone components, zoneless change detection and signals. ADR-0008 governs its integration constraints: app-origin Trusted Types allows `angular` only, styles use a nonce, initial JavaScript/CSS must remain within 300 kB gzip and each lazy chunk within 150 kB gzip. `src/styles/tokens.css` is the existing source of light, explicit dark and OS-selected dark tokens.

The carried item **UI library evaluation ADR (S211)** needed a library decision, not another per-screen redesign. This ADR resolves that decision. Closing the carried status row belongs to the coordinator's close-out; foundation compilation, component QA and browser CSP evidence remain separate acceptance checks.

## Decision

1. `[DECISION]` Use `@spartan-ng/brain` **1.9.0** for behavior and copy the matching Helm source into `apps/web/src/app/ui/`. Consumers import individual families through `@evidgate/ui/<family>`. Do not add a second npm-packaged styled component library.
2. `[DECISION]` Use **Tailwind CSS 4.3.4** through `@tailwindcss/postcss` **4.3.4** and PostCSS **8.5.32** in `apps/web/.postcssrc.json`. Keep Angular's existing builder. No runtime Tailwind compiler, CDN stylesheet or install-time script is added.
3. `[DECISION]` Keep `tokens.css` unchanged. `spartan-theme.css` aliases semantic variables to existing `--eg-*` tokens and exposes them with `@theme inline`. The same aliases inherit different light/dark token values. `dark:` understands both `data-theme` and the OS preference, rather than introducing a `.dark` state store.
4. `[DECISION]` Retain only button, input, label, select, checkbox, dialog, dropdown-menu, tabs, table, badge, tooltip, sonner, skeleton, alert, card and separator, plus their shared helpers. Keep their variant APIs; no screen/layout migration is included.
5. `[DECISION]` Adapt only the matching Vega recipe sections, split by family under `src/styles/helm/`. Do not install all upstream themes, global Tailwind Preflight or the upstream preset's global backdrop override. Preserve the existing CSS layer order and scan the adopted UI source explicitly. Future consumers using utility classes outside that directory must add their intended source scope deliberately.
6. `[DECISION]` Compile the copied UI directory even when it is not yet imported by a route, through `tsconfig.app.json`. This catches template/type defects without eagerly bundling every primitive.
7. `[DECISION]` Do not permit a new Trusted Types policy for icons. The upstream `ng-icons` rendering path writes strings to `template.innerHTML`. Replace its eleven required icons with fixed Lucide geometry compiled as Angular SVG templates (`HlmIcon`), and retain the upstream licence notices. There is no arbitrary SVG-string input and no `bypassSecurityTrust*` call.

### Dependencies and provenance

Exact direct versions and licences are registered in [ADR-0002](0002-compatibility-and-licence-matrix.md): Brain 1.9.0, clsx 2.1.1, class-variance-authority 0.7.1, tailwind-merge 3.7.0, tw-animate-css 1.4.0, Tailwind/PostCSS packages above. [Brain's published metadata](https://registry.npmjs.org/@spartan-ng/brain/1.9.0) declares Luxon an optional peer; it is not installed directly because no adopted primitive imports it. No new direct package declares a `postinstall` script; installation used `--ignore-scripts`.

`lightningcss` (MPL-2.0, unmodified, build-time only, never included in `dist`) is now also reached through [`@tailwindcss/node` 4.3.4](https://registry.npmjs.org/@tailwindcss/node/4.3.4) on the product build path; the existing licence-policy exception is left unchanged, and the owner may refresh its reason text, which still names only the vite/vitest path.

✅ Helm source and Vega sections came from the published [`@spartan-ng/cli@1.9.0` tarball](https://registry.npmjs.org/@spartan-ng/cli/1.9.0), following the official manual-copy route. The CLI is not a permanent project dependency. The copied MIT notice is `ui/LICENSE.spartan`. Fixed icon geometry came from `@ng-icons/lucide@33.4.0`; its Lucide/Feather notices are retained in `ui/icon/LICENSE`. Neither icon runtime package remains installed as a direct dependency.

### Public primitive contract

Each family exports its named declarations and an `Hlm…Imports` readonly array. Bind non-copy enum/metadata values as constants to comply with the template-i18n rule. Provide accessible labels from translated consumer content; a primitive is not a substitute for a labelled field.

| Import suffix | Main selector / declaration | Inputs and composition |
|---|---|---|
| `button` | `button[hlmBtn]`, `a[hlmBtn]`; `HlmButton` | `variant`: default/outline/secondary/ghost/destructive/link; `size`: default/xs/sm/lg/icon/icon-xs/icon-sm/icon-lg; Brain `disabled` |
| `input` | `[hlmInput]`; `HlmInput` | Native input properties; Brain input behavior and field-description integration |
| `label` | `[hlmLabel]`; `HlmLabel` | Native `for` / projected label content |
| `select` | `hlm-select`, `hlm-select-multiple`, `hlm-select-trigger`, `hlm-select-content`, `hlm-select-item` | Brain value/form APIs; trigger `size`, `buttonId`, `forceInvalid`, `aria-invalid`; content `showScroll`; portal, value, placeholder, group and separator helpers |
| `checkbox` | `hlm-checkbox`; `HlmCheckbox` | `checked`/`checkedChange`, `indeterminate`, `disabled`, `required`, `inputId`, `name`, aria-label/labelledby/describedby; ControlValueAccessor |
| `dialog` | `hlm-dialog`, `hlm-dialog-content`, `[hlmDialogTrigger]`, `[hlmDialogPortal]`, `[hlmDialogClose]` | Brain state/focus lifecycle; content `showCloseButton`, `closeLabel`; `HlmDialogService.open`; header/title/description/footer |
| `dropdown-menu` | `[hlmDropdownMenu]`, `[hlmDropdownMenuTrigger]`, `[hlmDropdownMenuItem]` | Trigger `align`/`side` and Brain trigger inputs; item `disabled`, `variant`, `inset`; checkbox/radio items, nested menus and indicators |
| `tabs` | `[hlmTabs]`, `[hlmTabsList]`, `[hlmTabsTrigger]`, `[hlmTabsContent]` | Required `tab`; matching trigger/content key; list variant default/line; lazy content and paginated list |
| `table` | `table[hlmTable]`, `div[hlmTableContainer]`, `thead[hlmTHead]`, `tbody[hlmTBody]`, `tr[hlmTr]`, `th[hlmTh]`, `td[hlmTd]` | Native table semantics; caption and footer helpers; no sorting/data source/virtualization replacement |
| `badge` | `[hlmBadge]`; `HlmBadge` | `variant`: default/secondary/destructive/outline/ghost/link |
| `tooltip` | `[hlmTooltip]`; `HlmTooltip` | Text/template content, `position`, `hideDelay`, `showDelay`, `sideOffset`, `tooltipDisabled` |
| `sonner` | `hlm-toaster`; `HlmToaster` | `theme`, `position`, `hotKey`, `duration`, `visibleToasts`, `expand`, `closeButton`, `richColors`, `toastOptions`, `offset`; dispatch through Brain `toast` |
| `skeleton` | `[hlmSkeleton]`, `hlm-skeleton` | Consumer controls geometry and accessible loading announcement |
| `alert` | `[hlmAlert]`; `HlmAlert` | `variant`: default/destructive; title, description and action helpers |
| `card` | `[hlmCard]`, `hlm-card`; `HlmCard` | `size`: default/sm; title, description, header, content, action and footer helpers |
| `separator` | `[hlmSeparator]`, `hlm-separator` | Brain `orientation` and `decorative` semantics |
| `utils` | `classes`, `hlm` | Shared class merging |

## Alternatives rejected

Bundle figures below are deliberately not inferred from npm unpacked sizes. None of the alternatives was installed in the application for an apples-to-apples gzip comparison.

| Alternative | Angular 22 and licence | Accessibility | Token fit / migration cost | Bundle evidence / reason rejected |
|---|---|---|---|---|
| CDK only | ✅ Existing CDK 22.2.4; MIT ([registry](https://registry.npmjs.org/@angular/cdk/22.2.4)) | Existing overlay/table mechanisms remain; ⚠️ app-specific keyboard and screen-reader coverage must still be exercised | `[INFERENCE]` Direct token fit, but the team owns every visual control and interaction composition | ⚠️ Incremental bundle not measured. Rejected as the sole UI layer because it leaves the repeated styled-control work with the application |
| Angular Material | ✅ 22.2.5 declares Angular `^22`/`^23`; exact CDK 22.2.5 peer, so adopting that patch requires a CDK bump; MIT ([registry](https://registry.npmjs.org/@angular/material/22.2.5)) | ⚠️ No screen-reader audit performed for this app | `[INFERENCE]` Material appearance/theming adds translation between the existing compact token language and Material controls; replacement cost higher than owned Helm styles | ⚠️ App gzip not measured. Rejected on visual/token ownership, not a claimed size disadvantage |
| PrimeNG | ✅ 22.1.4 peers Angular `^22.1`; [registry](https://registry.npmjs.org/primeng/22.1.4). ✅ Community MIT; LTS suffix has separate commercial terms ([licence](https://github.com/primefaces/primeng/blob/master/LICENSE.md)) | ⚠️ No app-specific accessibility validation performed | `[INFERENCE]` Broad widget suite, but adds a separate styled-widget/theming surface and more migration decisions | ⚠️ App gzip not measured. Rejected because the foundation needs composable primitives, not a broader widget-system replacement |
| NG-ZORRO | ✅ 22.1.2 peers Angular `^22`; MIT ([registry](https://registry.npmjs.org/ng-zorro-antd/22.1.2)) | ⚠️ No app-specific accessibility validation performed | `[INFERENCE]` Ant Design conventions diverge from existing Evidgate tokens and density; higher visual migration cost | ⚠️ App gzip not measured. Rejected on design-system fit, not an unverified accessibility or size claim |
| Spartan + Tailwind | ✅ Brain 1.9.0 peers Angular/CDK `>=21 <23`, MIT ([registry](https://registry.npmjs.org/@spartan-ng/brain/1.9.0)); [support policy](https://www.spartan.ng/documentation/version-support) explicitly lists Angular 22 | ✅ Official installation describes accessible headless primitives; ⚠️ this is not proof of WCAG conformance in Evidgate | `[INFERENCE]` Owned Helm source makes the `--eg-*` mapping explicit and incremental; ongoing copied-source maintenance is accepted | ⚠️ Final app gzip pending the workspace SDK build prerequisite. Chosen for ownership and token fit |

## Consequences

- Existing screen layouts and the shared data table remain untouched. Lists above 200 rows still use the established virtualized table contract; the native Helm table does not replace it.
- Brain retains its maintained behavior layer; Helm updates require reviewing the copied-source delta, not blindly regenerating it. Required local adaptations include translated close copy, static icon templates, token-backed arbitrary dimensions, strict type imports and deferred self-provider references.
- Tailwind Preflight is intentionally omitted to avoid resetting old screens. Adopted primitives receive scoped box sizing/border/font defaults. New utility source scopes are explicit, avoiding accidental utility styles on unrelated legacy classes.
- Theme tests parse CSS text with PostCSS, because jsdom cannot prove custom-property resolution. They cover light/dark maps, existing-token references, Tailwind exports and rejected raw/unknown references. They do not claim visual contrast or computed-style coverage.
- Focus and coarse-pointer targets use the existing focus/control tokens. Reduced-motion overrides are scoped to the adopted primitives. Long content, overlay focus restoration, checkbox semantics and toast announcements still need the integration QA/browser pass before production adoption.
- ✅ No CSP header, nonce handling or Trusted Types allowlist changes are made. `[INFERENCE]` Static compiled icons remove the discovered icon string sink. ⚠️ Full Brain overlay/toast behavior under the deployed CSP still requires the existing browser CSP gate; build success alone cannot establish it.
- `[DECISION]` Coordinator-approved lint exceptions preserve upstream Helm diffability: only `apps/web/src/app/ui/**/*.ts` permits selector prefixes `app` and `hlm` and disables `@angular-eslint/no-input-rename`. Outside that directory the `app`-only selector policy remains unchanged. No vendored Brain code or `brn` prefix exception is added. Type safety, template accessibility, i18n and the other rules remain enabled.
- `[DECISION]` The installed `@eslint/css` 2.0.1 accepts a custom syntax object, not a built-in `"tailwind"` mode. Its [documented Tailwind integration](https://github.com/eslint/css#tailwind-css) requires the additional `tailwind-csstree` package. The coordinator prohibited adding a lint dependency and authorized fallback B: ESLint ignores only `apps/web/src/styles/spartan-*.css` and `apps/web/src/styles/helm/**/*.css`. **The CSS style-token lint rule does not cover those files.** Angular/PostCSS still parses them, and the theme contract spec checks alias integrity, but neither replaces full raw-literal lint coverage. All other stylesheets and inline Angular styles retain their existing checks. Restore parser-backed lint coverage when a Tailwind parser dependency is approved.
- The fixed package pins and root lockfile must move together. MIT/Apache-2.0 direct dependencies are covered by the existing licence policy; copied icon notices include ISC and Feather MIT. Transitive licence/SBOM scanning remains a final gate, not a claim inferred from direct metadata.

## Evidence

Prior art read before implementation:

- ✅ [Spartan installation](https://www.spartan.ng/documentation/installation): npm Brain plus copied Helm; took the layer separation and manual setup. Did not copy its literal palette or global reset.
- ✅ [Version support](https://www.spartan.ng/documentation/version-support): two latest Angular majors, currently 21 and 22. Brain's published peer range independently admits the installed Angular/CDK pins.
- ✅ [Theming](https://www.spartan.ng/documentation/theming): semantic background/foreground variables and `@theme inline`. Reused that naming contract, not its OKLCH values.
- ✅ [CLI](https://www.spartan.ng/documentation/cli): selectively copy primitive families and dependencies. Used the exact published CLI templates instead of introducing Nx or retaining the generator as a dependency.
- ✅ [Tailwind Angular guide](https://tailwindcss.com/docs/installation/framework-guides/angular): PostCSS plugin route. Did not use the guide's `--force`; peers resolved with ordinary npm install.
- ✅ [Angular 22.2.3 registry metadata](https://registry.npmjs.org/@angular/core/22.2.3) permits Node `>=26.0.0`; helper engine ranges are recorded in ADR-0002. ⚠️ A missing engines field is not a vendor Node-26 certification.

Executed in the foundation worktree:

- `npm install --workspace apps/web --save-exact --ignore-scripts ...` and matching dev-dependency installation completed without peer override flags.
- The initial `npm ls --workspace apps/web @spartan-ng/brain tailwindcss @tailwindcss/postcss clsx class-variance-authority tailwind-merge luxon tw-animate-css postcss --depth=0` exited 0 with the nine pins then installed. Luxon was subsequently removed after confirming that Brain declares it optional and the adopted primitives do not import it.
- `npx ng build --progress=false` ran once. It failed because `@evidgate/sdk` could not resolve in the fresh worktree, with cascading existing consumer errors. It also exposed three copied-source declaration problems (type-only `HlmCardConfig`, checkbox/radio `forwardRef`), corrected after the run. No Tailwind/PostCSS error was reported. ⚠️ This is not a successful final compilation, and the correction has not been re-built inside this slice.
- ⚠️ Bundle-budget validation cannot claim a result without a successful current build. No before/after gzip numbers were fabricated. The orchestrator must build the workspace SDK, run the final web build and `node scripts/check-bundle-budget.mjs`.
- Theme spec written at `apps/web/src/styles/spartan-theme.spec.ts`; not executed inside the implementation slice. Final QA owns its execution and primitive interaction/CSP coverage.

## Revisit trigger

Reopen this decision if Spartan drops Angular 22 before the application's upgrade, any runtime primitive requires a Trusted Types policy beyond `angular`, an overlay cannot honor the existing nonce/CSP, token or template lint cannot express the intended styles without gate changes, the final gzip budgets are exceeded, or copied-source maintenance becomes more costly than the controls it replaces. A per-screen migration is a separate lane with its own visual and accessibility evidence.
