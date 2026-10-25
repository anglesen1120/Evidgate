# Web design tokens — 09.1.1-T11.2

## Purpose and source

Port the owner-approved Web UI v2 palette and scales; this document does not approve new screens.
The application token source is [`tokens.css`](../../../apps/web/src/styles/tokens.css).
The reference is the read-only [v2 stylesheet](../web-ui/output/assets/evidgate.css): layer order at
line 1; light tokens at lines 4–91; dark overrides at lines 93–117; token layer ends at line 118.
The [task contract](../../../plans/features/09-web-ui/09.1.1-app-scaffold-and-build-acceptance-npm.md),
§10, line 221, requires colour, type, spacing, radius, elevation, both themes and recorded contrast.

Every one of the 79 v2 custom-property names is retained. The 22 v2 dark overrides are retained.
All values are unchanged; font-family quoting is normalized from double to single quotes without
changing the font-family values. Additions are separately identified below and in CSS comments.
No components, font downloads, dependencies or application bootstrap changes are included.

Completeness, outcome and decision remain separate data fields. A missing or pending result is not
PASS. The `pending` palette is independently named and neutral: it cannot imply success, failure or
warning. Colour never replaces a text label or icon with an accessible name.

## How to run contrast.py

From the worktree root:

```sh
python3 docs/design/web/contrast.py
```

The [stdlib-only script](contrast.py) locates `apps/web/src/styles/tokens.css` relative to its own
location, so the current directory does not affect resolution. It reads the base and both dark
blocks, resolves simple token aliases, converts opaque sRGB colours to relative luminance, and
calculates `(lighter + 0.05) / (darker + 0.05)`. Decisions use full precision; displayed ratios use
two decimals. Exit status is 1 for any failed pair, missing/invalid colour, unreadable source,
malformed supported block, duplicate declaration or unequal dark declaration maps; otherwise 0.
It is intentionally a checker for this file's flat token blocks, not a general CSS cascade parser.

Ordinary text requires 4.5:1. Non-text control boundaries and focus require 3:1. The only separate
large-text check is `text / bg` for the v2 page-header display title at a computed size of at least
24px: its `clamp(22px, 2vw, 28px)` reaches 24px at a viewport width of 1200px
(v2 CSS lines 613–615). Below that width its existing ordinary-text 4.5:1 check still applies.
The 22px `--eg-text-xl` KPI values use weight 600, not bold 700 (lines 1308–1313 and 3357–3362),
so they are **not** granted the ≥18.66px bold-text exception. No text pair is tested only at 3:1.

Alpha `--eg-scrim: #10202b99` is an overlay, not a foreground/background text pair. Its composited
appearance depends on the backdrop and is excluded, not treated as opaque or awarded a text PASS.
These checks cover the listed token combinations, not every possible future component combination
or focus-ring geometry, keyboard behaviour, browser rendering or whole-screen accessibility.

## Token inventory

### Colour roles

Values are opaque except the explicitly marked scrim. An error uses `danger`, matching v2 naming;
there is no additional `--eg-error-*` alias.

| Token | Light | Dark | Role |
| --- | --- | --- | --- |
| `--eg-bg` | `#f5f8fa` | `#10202b` | Page canvas |
| `--eg-surface` | `#ffffff` | `#14232d` | Cards, menus and dialogs |
| `--eg-surface-raised` | `#f5f8fa` | `#192d39` | Raised/hover surface |
| `--eg-text` | `#172b3a` | `#e5eef4` | Primary text |
| `--eg-text-muted` | `#526471` | `#a8bac7` | Secondary text, not disabled-by-opacity text |
| `--eg-border` | `#dce5eb` | `#334a59` | Decorative separation; not the required control boundary |
| `--eg-border-control` | `#7a8c99` | `#7f98a8` | Identifiable control boundary |
| `--eg-accent` | `#006b66` | `#76d8cb` | Links, controls and active accents |
| `--eg-accent-soft` | `#e8f5f2` | `#153d3b` | Accent-tinted background |
| `--eg-accent-fg` | `var(--eg-accent)` → `#006b66` | `var(--eg-accent)` → `#76d8cb` | Added explicit accent text alias |
| `--eg-on-accent` | `#ffffff` | `#10202b` | Label on solid accent |
| `--eg-focus` | `#006b66` | `#76d8cb` | Visible focus ring |
| `--eg-scrim` | `#10202b99` | `#10202b99` | Alpha overlay; excluded from text contrast |
| `--eg-success-fg` | `#11633d` | `#90dcad` | Success text |
| `--eg-success-bg` | `#e9f5ee` | `#153b2a` | Success background |
| `--eg-danger-fg` | `#a62d37` | `#ffb2ba` | Error/failure text |
| `--eg-danger-bg` | `#fceef0` | `#49242b` | Error/failure background |
| `--eg-warning-fg` | `#805500` | `#f2cd7a` | Warning text |
| `--eg-warning-bg` | `#fff5dc` | `#40341c` | Warning background |
| `--eg-info-fg` | `#245ea1` | `#a7cbfa` | Informational text |
| `--eg-info-bg` | `#ecf3fc` | `#1c3555` | Informational background |
| `--eg-neutral-fg` | `#526471` | `#b8c7d2` | General neutral text |
| `--eg-neutral-bg` | `#eff3f6` | `#263844` | General neutral background |

### Pending palette

[DECISION] Use the v2 neutral grey-blue values, but give pending its own literal-valued properties
so its domain meaning cannot accidentally inherit a verdict colour. None references success,
danger or warning; pending is not an amber warning and is not a green PASS.

| Token | Light | Dark |
| --- | --- | --- |
| `--eg-pending-fg` | `#526471` | `#b8c7d2` |
| `--eg-pending-bg` | `#eff3f6` | `#263844` |
| `--eg-pending-border` | `#7a8c99` | `#7f98a8` |

### Type

These values are shared by both themes. Token declarations do not load font assets.

| Token | Value |
| --- | --- |
| `--eg-font-display` | `'Manrope', 'Public Sans', system-ui, sans-serif` |
| `--eg-font-body` | `'Public Sans', system-ui, -apple-system, 'Segoe UI', sans-serif` |
| `--eg-font-mono` | `'SFMono-Regular', Consolas, 'Liberation Mono', monospace` |
| `--eg-weight-regular` | `400` |
| `--eg-weight-medium` | `500` |
| `--eg-weight-semibold` | `600` |
| `--eg-weight-bold` | `700` |

| Size token | Value | Line-height token | Value |
| --- | --- | --- | --- |
| `--eg-text-xs` | `12px` | `--eg-leading-xs` | `18px` |
| `--eg-text-sm` | `14px` | `--eg-leading-sm` | `20px` |
| `--eg-text-md` | `16px` | `--eg-leading-md` | `24px` |
| `--eg-text-lg` | `18px` | `--eg-leading-lg` | `26px` |
| `--eg-text-xl` | `22px` | `--eg-leading-xl` | `30px` |
| `--eg-text-2xl` | `28px` | `--eg-leading-2xl` | `36px` |

### Spacing

Shared by both themes. Do not substitute a familiar spacing scale: v2 `space-4` is 12px, not 16px.

| Token | Value |
| --- | --- |
| `--eg-space-0` | `0` |
| `--eg-space-1` | `2px` |
| `--eg-space-2` | `4px` |
| `--eg-space-3` | `8px` |
| `--eg-space-4` | `12px` |
| `--eg-space-5` | `16px` |
| `--eg-space-6` | `20px` |
| `--eg-space-7` | `24px` |
| `--eg-space-8` | `32px` |
| `--eg-space-9` | `40px` |
| `--eg-space-10` | `48px` |
| `--eg-space-11` | `56px` |
| `--eg-space-12` | `64px` |

### Radius

| Token | Light and dark |
| --- | --- |
| `--eg-radius-sm` | `4px` |
| `--eg-radius-md` | `8px` |
| `--eg-radius-lg` | `12px` |
| `--eg-radius-pill` | `999px` |
| `--eg-border-width` | `1px` |

### Elevation

[DECISION] Name the actual v2 effects rather than invent a blurred-shadow hierarchy. These levels
are semantic references, **not** four progressively stronger shadows. In particular level 2 is
flat: v2 menus, drawers, dialogs and toasts have borders/backdrops but no declared box shadow
(lines 1437–1463, 1518–1537, 1591–1606 and 3571–3586). No dark alpha adjustment is needed for
parity; the border/scrim references resolve per theme.

| Token | Light | Dark | Intended reference |
| --- | --- | --- | --- |
| `--eg-elevation-0` | `none` | `none` | Flat baseline; `none` also appears on disabled buttons, line 983 |
| `--eg-elevation-1` | `0 1px 0 var(--eg-border)` | `0 1px 0 var(--eg-border)` | Sticky-table separator, line 3015 |
| `--eg-elevation-2` | `none` | `none` | Flat bordered menu/dialog/toast; depth comes from z-index/backdrop |
| `--eg-elevation-3` | `0 0 0 100vmax var(--eg-scrim)` | `0 0 0 100vmax var(--eg-scrim)` | Navigation scrim, lines 554 and 4082, outside the component layer |

Complete `@layer components` shadow scan (the layer begins at line 835) found the following
**six distinct values**, with repeated occurrences listed. Inset states are not elevation and
must not be repurposed as overlay shadows:

| Actual `box-shadow` value | v2 CSS lines | Classification |
| --- | --- | --- |
| `inset 0 0 0 2px var(--eg-on-accent)` | 910, 915, 955 | Button interaction cue |
| `none` | 983 | Disabled button reset |
| `inset 0 0 0 1px var(--eg-accent)` | 1263 | Selected card outline |
| `inset 0 0 0 0 transparent` | 1782 | Tree pseudo-element baseline |
| `inset 0 0 0 var(--eg-focus-width) var(--eg-focus)` | 1801 | Tree focus cue |
| `0 1px 0 var(--eg-border)` | 3015 | Sticky-table separator |

The navigation scrim occurs in the layout/utilities layers, not in components. Applying its
`100vmax` spread to menus or toasts would incorrectly obscure the page. These token definitions
alone do not apply any shadows to components.

### Control sizes and layout dimensions

| Token | Light and dark |
| --- | --- |
| `--eg-control-sm` | `32px` |
| `--eg-control-md` | `36px` |
| `--eg-control-lg` | `40px` |
| `--eg-control-touch` | `44px` |
| `--eg-row-compact` | `40px` |
| `--eg-row-default` | `44px` |
| `--eg-nav-width` | `224px` |
| `--eg-nav-rail` | `68px` |
| `--eg-rail-width` | `320px` |
| `--eg-focus-width` | `2px` |
| `--eg-focus-offset` | `2px` |

A compact visual control is not permission to shrink a touch target below 44×44px. Layout and
small-screen behaviour are specified in [layout.md](layout.md), including the 360px no-horizontal-
scroll requirement; tokens alone do not implement responsive behaviour.

### Motion

| Token | Light and dark |
| --- | --- |
| `--eg-duration-fast` | `150ms` |
| `--eg-duration-standard` | `200ms` |
| `--eg-ease` | `cubic-bezier(0.2, 0, 0, 1)` |

No reduced-motion component rule is added here. V2 already handles it in its utilities layer,
lines 4465–4479, including its indeterminate progress fallback. A component port must retain that
behaviour rather than assuming the token file alone disables animations.

`--eg-duration-fast` and `--eg-duration-standard` are the only durations a component may write:
the lint rule reports a non-zero `ms`/`s` time in `transition`/`animation` as `rawDuration`
("Lint rule `style-tokens`" below). `--eg-ease` is a review convention, **not** an enforced
literal: a timing function (`cubic-bezier(...)`, `steps(...)`, `ease`/`linear`) carries no unit the
scanner can key on, so neither rule reports one (see "Deliberately not checked").

### Z-index

| Token | Light and dark |
| --- | --- |
| `--eg-z-base` | `0` |
| `--eg-z-sticky` | `10` |
| `--eg-z-menu` | `20` |
| `--eg-z-drawer` | `30` |
| `--eg-z-dialog` | `40` |
| `--eg-z-toast` | `50` |

### Breakpoints

| Token | Light and dark | Meaning |
| --- | --- | --- |
| `--eg-bp-tablet` | `768px` | Drawer below; icon rail starts here |
| `--eg-bp-desktop` | `1280px` | Expanded navigation starts here |

[DECISION] These additions expose v2's existing boundaries for documentation and JavaScript
readability. CSS custom properties **cannot be used inside media-query conditions**. Use the
same literal boundaries in CSS queries and keep them synchronized with these tokens; do not write
`@media (min-width: var(--eg-bp-desktop))`. Source: v2 JavaScript line 281.

## Themes

[DECISION] The resolution contract is:

| User choice on `html` | OS preference | Resolved palette / native `color-scheme` |
| --- | --- | --- |
| No `data-theme` attribute | Light or unavailable | Light |
| No `data-theme` attribute | Dark | Dark |
| `data-theme='light'` | Either | Light |
| `data-theme='dark'` | Either | Dark |

`:root` supplies light by default. The dark media query applies to
`:root:not([data-theme='light'])`; the explicit `html[data-theme='dark']` selector continues to
work independently of OS preference. Single or double attribute-selector quotes are equivalent,
so the original `html[data-theme="dark"]` contract is preserved. Explicit light needs no duplicate
light block: it excludes the media-query override and inherits the base light values.

Plain CSS has no declaration-block mixin. To retain flat, directly consumable `--eg-*` values and
the original dark selector, the dark declarations are repeated once inside the media query.
`contrast.py` removes comments, parses the complete declaration maps (including `color-scheme`),
and exits 1 if a property name or value differs. Keep both dark blocks identical.

The read-only [v2 JavaScript](../web-ui/output/assets/evidgate.js) reads `evidgate-theme` at line 278,
sets `html[data-theme]` at line 416, and persists the explicit light/dark choice at line 417.
It reads `evidgate-locale` at line 277 and writes it at line 429; `applyLocale` sets `html.lang`
at line 428. The supported locale choices are `vi` and `en`; locale does not change token values.

The v2 script defaults to explicit light unless the root is already dark (line 278); it does not
implement the new no-preference OS-resolution branch. The Angular shell must read an existing
stored light/dark choice before applying the attribute. With no saved explicit choice, it must
leave/remove `data-theme` to allow the media query. CSS cannot read localStorage itself. No new
persisted `system` string, locale behaviour or preference-control UI is introduced by this slice.

## Contrast table

Output of `python3 docs/design/web/contrast.py` on 2026-10-24, exit status **0**. Pair names omit
only the shared `--eg-` prefix. This table and the three summary lines reproduce script output.

| Theme | Pair (--eg- prefix) | Usage | Ratio | Minimum | Result |
| --- | --- | --- | ---: | ---: | --- |
| light | text / bg | text | 13.64 | 4.5 | PASS |
| light | text / surface | text | 14.55 | 4.5 | PASS |
| light | text / surface-raised | text | 13.64 | 4.5 | PASS |
| light | text / accent-soft | text | 13.01 | 4.5 | PASS |
| light | text-muted / bg | text | 5.76 | 4.5 | PASS |
| light | text-muted / surface | text | 6.14 | 4.5 | PASS |
| light | text-muted / surface-raised | text | 5.76 | 4.5 | PASS |
| light | text-muted / accent-soft | text | 5.49 | 4.5 | PASS |
| light | text-muted / neutral-bg | disabled action text | 5.50 | 4.5 | PASS |
| light | accent / bg | link text | 5.98 | 4.5 | PASS |
| light | accent / surface | link text | 6.37 | 4.5 | PASS |
| light | on-accent / accent | button label | 6.37 | 4.5 | PASS |
| light | success-fg / success-bg | status text | 6.52 | 4.5 | PASS |
| light | success-fg / surface | status text | 7.30 | 4.5 | PASS |
| light | danger-fg / danger-bg | status text | 6.10 | 4.5 | PASS |
| light | danger-fg / surface | status text | 6.88 | 4.5 | PASS |
| light | warning-fg / warning-bg | status text | 6.01 | 4.5 | PASS |
| light | warning-fg / surface | status text | 6.53 | 4.5 | PASS |
| light | info-fg / info-bg | status text | 5.89 | 4.5 | PASS |
| light | info-fg / surface | status text | 6.58 | 4.5 | PASS |
| light | neutral-fg / neutral-bg | status text | 5.50 | 4.5 | PASS |
| light | neutral-fg / surface | status text | 6.14 | 4.5 | PASS |
| light | pending-fg / pending-bg | status text | 5.50 | 4.5 | PASS |
| light | pending-fg / surface | status text | 6.14 | 4.5 | PASS |
| light | accent-fg / accent-soft | accent text | 5.70 | 4.5 | PASS |
| light | border-control / surface | non-text border | 3.48 | 3.0 | PASS |
| light | border-control / bg | non-text border | 3.26 | 3.0 | PASS |
| light | focus / surface | non-text focus | 6.37 | 3.0 | PASS |
| light | focus / bg | non-text focus | 5.98 | 3.0 | PASS |
| light | focus / surface-raised | non-text focus | 5.98 | 3.0 | PASS |
| light | accent / surface | non-text control | 6.37 | 3.0 | PASS |
| light | pending-border / pending-bg | non-text border | 3.12 | 3.0 | PASS |
| light | pending-border / surface | non-text border | 3.48 | 3.0 | PASS |
| light | pending-border / bg | non-text border | 3.26 | 3.0 | PASS |
| light | text / bg | large display >=24px | 13.64 | 3.0 | PASS |
| dark | text / bg | text | 14.14 | 4.5 | PASS |
| dark | text / surface | text | 13.66 | 4.5 | PASS |
| dark | text / surface-raised | text | 12.11 | 4.5 | PASS |
| dark | text / accent-soft | text | 10.14 | 4.5 | PASS |
| dark | text-muted / bg | text | 8.32 | 4.5 | PASS |
| dark | text-muted / surface | text | 8.04 | 4.5 | PASS |
| dark | text-muted / surface-raised | text | 7.12 | 4.5 | PASS |
| dark | text-muted / accent-soft | text | 5.96 | 4.5 | PASS |
| dark | text-muted / neutral-bg | disabled action text | 6.07 | 4.5 | PASS |
| dark | accent / bg | link text | 9.86 | 4.5 | PASS |
| dark | accent / surface | link text | 9.52 | 4.5 | PASS |
| dark | on-accent / accent | button label | 9.86 | 4.5 | PASS |
| dark | success-fg / success-bg | status text | 7.70 | 4.5 | PASS |
| dark | success-fg / surface | status text | 9.96 | 4.5 | PASS |
| dark | danger-fg / danger-bg | status text | 7.85 | 4.5 | PASS |
| dark | danger-fg / surface | status text | 9.43 | 4.5 | PASS |
| dark | warning-fg / warning-bg | status text | 7.99 | 4.5 | PASS |
| dark | warning-fg / surface | status text | 10.54 | 4.5 | PASS |
| dark | info-fg / info-bg | status text | 7.44 | 4.5 | PASS |
| dark | info-fg / surface | status text | 9.61 | 4.5 | PASS |
| dark | neutral-fg / neutral-bg | status text | 7.01 | 4.5 | PASS |
| dark | neutral-fg / surface | status text | 9.28 | 4.5 | PASS |
| dark | pending-fg / pending-bg | status text | 7.01 | 4.5 | PASS |
| dark | pending-fg / surface | status text | 9.28 | 4.5 | PASS |
| dark | accent-fg / accent-soft | accent text | 7.07 | 4.5 | PASS |
| dark | border-control / surface | non-text border | 5.32 | 3.0 | PASS |
| dark | border-control / bg | non-text border | 5.51 | 3.0 | PASS |
| dark | focus / surface | non-text focus | 9.52 | 3.0 | PASS |
| dark | focus / bg | non-text focus | 9.86 | 3.0 | PASS |
| dark | focus / surface-raised | non-text focus | 8.44 | 3.0 | PASS |
| dark | accent / surface | non-text control | 9.52 | 3.0 | PASS |
| dark | pending-border / pending-bg | non-text border | 4.02 | 3.0 | PASS |
| dark | pending-border / surface | non-text border | 5.32 | 3.0 | PASS |
| dark | pending-border / bg | non-text border | 5.51 | 3.0 | PASS |
| dark | text / bg | large display >=24px | 14.14 | 3.0 | PASS |

```text
Dark declaration blocks: PASS (identical property names and values).
Excluded: --eg-scrim (#RRGGBBAA); backdrop alpha is not a text pair.
Result: 70/70 pairs PASS; 0 FAIL.
```

## Failures and fixes

No listed v2 pair fails and no v2 value needs changing. The additional pending text and border
pairs also pass. There are no unrecorded contrast failures. No PASS is inferred for an unlisted
foreground/background pairing; add it to `pairs()` and regenerate this table before approving its
use. The subdued decorative `--eg-border` is deliberately not a control-boundary substitute.

## Additions beyond v2

Each addition has a `[DECISION]` comment at its CSS declaration or enclosing added block.

| Addition | Reason |
| --- | --- |
| `--eg-pending-fg`, `--eg-pending-bg`, `--eg-pending-border` | Task-required neutral verdict-free palette; reuse literal v2 neutral/control colours without coupling to verdict tokens |
| `--eg-accent-fg: var(--eg-accent)` | Explicit text-role name for the requested accent-fg/accent-soft contrast check; keeps the actual v2 accent unchanged |
| `--eg-elevation-0` through `--eg-elevation-3` | Name existing flat surfaces, sticky separator and navigation scrim without adding new visuals; levels 0 and 2 intentionally coincide |
| `--eg-bp-tablet`, `--eg-bp-desktop` | Expose the existing 768px/1280px navigation boundaries for documentation/JS; not CSS media-query variables |
| Dark preference media query plus `color-scheme` | Task-required OS preference with explicit light/dark override and appropriately themed native controls |

There are ten additional custom properties, no renamed/removed v2 properties, and no new palette
hues, shadow recipes, font families, spacing sizes or reduced-motion component rules.

## Parity with v2

Every v2 token is present with the same value; **there are no value exceptions**. Font-family
quote normalization is syntactic only. The original layer-order statement and `@layer tokens`
wrapper are retained, and the original explicit-dark selector remains semantically equivalent.
A read-only comparison against the v2 token source found zero name/value mismatches in either
resolved theme: 79 original names per theme, including the 22 original dark overrides.

The additions above do not alter the read-only mockups. No component style is silently ported
with these tokens. See the elevation audit for why an invented four-step blurred-shadow scale
would not have been a faithful v2 port.

## Rules for components

- Use the existing token names for colours, font families, type, spacing, borders, radius,
  dimensions, elevation, stacking and motion. Do not introduce raw hex colours, raw pixel values or
  raw `ms`/`s` durations outside `tokens.css` in component styles. This is enforced by the
  `style-tokens` ESLint rule of **09.1.1-T11.4**, documented in § Lint rule `style-tokens` below;
  the documented literal media-query boundaries are its only syntax exception.
- Use `--eg-border-control` for a boundary needed to identify an input/control; `--eg-border` is
  decorative. Keep focus visible using the focus colour, width and offset tokens.
- Pair text with the backgrounds recorded above. Use `--eg-on-accent` on solid accent buttons;
  do not assume the same label works on every verdict-coloured button or on an alpha scrim.
- Render pending with its own neutral tokens and a visible pending label. Never render missing or
  pending data as PASS. Do not collapse completeness, outcome and decision into a colour.
- Reuse the v2 flat overlay treatment. Do not apply the navigation-scrim elevation token to other
  surfaces. Healing remains a proposed PR, never a UI action that directly applies a repair.
- Honour theme switching without substituting hardcoded light values; honour reduced motion when
  porting interaction rules. Preserve the 44×44px touch target and 360px overflow requirements.

## Open points

- [OPEN] Application consumption and preference initialization belong to the Angular component/
  shell work, not these three files. The lane replacing `apps/web` must import `tokens.css`,
  preserve stored explicit light/dark choices, and leave `data-theme` unset when no choice exists
  if OS preference is to apply. No integrated-theme behaviour is claimed by this token-only slice.
- [CLOSED 09.1.1-T11.4] The raw-literal lint rule is implemented and its media-query syntax
  exception is recorded in § Lint rule `style-tokens` below.
- [OPEN] Any further real component foreground/background pair stays with the component tasks
  (09.1.1-T7/T8): add the pair to `pairs()` in [contrast.py](contrast.py), regenerate the table
  and only then use it. 09.1.1-T11.4 added `--eg-text-muted` on `--eg-neutral-bg` (the v2
  disabled-action recipe used by the [state sheet](state-sheet.md)). The token checker is not a
  substitute for component state, focus geometry or whole-screen accessibility checks.

## Lint rule `style-tokens`

Enforcement of the rules above is the `style-tokens` ESLint plugin in
[`apps/web/eslint-rules/`](../../../apps/web/eslint-rules/) (`style-tokens.js` plus one shared
detector, `raw-literal-detector.js`). It is wired in the root [`eslint.config.js`](../../../eslint.config.js)
and runs under `npm run lint`, so a raw literal fails the gate. Two rules share one detector:

| Rule | Applies to | Contract |
| --- | --- | --- |
| `style-tokens/no-raw-literals` | `apps/web/**/*.css` through the `css/css` language of `@eslint/css` 2.0.1, except `apps/web/src/styles/tokens.css` | a component stylesheet may only reference tokens |
| `style-tokens/no-raw-literals-inline` | `apps/web/**/*.ts`, non-spec | the same contract for the `styles: [...]` strings of an `@Component` |

**Checked in every declaration value** (custom properties, `var(--x, <fallback>)` fallbacks and
the inside of `calc()` included):

| Literal | Message id | Where |
| --- | --- | --- |
| hex colour, 3/4/6/8 digits | `rawHexColour` | any property |
| `rgb()`, `rgba()`, `hsl()`, `hsla()`, `hwb()`, `lab()`, `lch()`, `oklab()`, `oklch()`, `color()` | `rawColourFunction` | any property |
| CSS named colour (`red`, `rebeccapurple`, …) | `rawNamedColour` | colour-bearing properties (`color`, `background*`, `border*`, `outline*`, `column-rule*`, `text-decoration*`, `text-emphasis*`, `fill`, `stroke*`, `box-shadow`, `text-shadow`, `caret-color`, `accent-color`, `scrollbar-color`, `filter`) and every custom property |
| non-zero `px`/`rem`/`em` length | `rawLength` | spacing (`margin*`, `padding*`, `gap`, `row-gap`, `column-gap`, `inset*`, `top`/`right`/`bottom`/`left`), scroll spacing (`scroll-margin*`, `scroll-padding*`, all longhands and logical forms), radius (`border-radius`, corner radii), border/outline width (including the `border`/`outline` shorthands), `outline-offset`, `font`, `font-size`, `line-height`, `box-shadow`, `text-shadow`, and **every custom property** |
| non-zero `ms`/`s` duration | `rawDuration` | `transition`, `transition-duration`, `transition-delay`, `animation`, `animation-duration`, `animation-delay`, and **every custom property** |

`--eg-border-width: 1px` exists, so a `1px` hairline is **not** an exception: write
`border: var(--eg-border-width) solid var(--eg-border)`. The same applies to the `--eg-focus-width`
outline.

**Custom properties are checked for lengths and durations as well as colours.** A local
`--local-gap: 8px` copies a raw length exactly like `gap: 8px` does, so it fails with `rawLength`;
`--local-fade: 120ms` copies a motion duration exactly like `transition: opacity 120ms` does, so it
fails with `rawDuration` — in a component stylesheet and in an inline `styles` entry alike.
`tokens.css` keeps declaring the raw scale because that file is exempt at config level.
Literal-free custom properties still pass:

| Declaration | Why it passes |
| --- | --- |
| `--x: 0` | the zero exemption (`0px`/`0rem`/`0s`/`0ms` too) |
| `--x: 1.5` | unitless number |
| `--x: 100%` | percentage |
| `--x: var(--eg-space-4)` | token reference |
| `--x: calc(var(--eg-space-2) * 2)` | `calc()` over a token reference only |

**Deliberately not checked.** The length regex covers `px`, `rem` and `em` and the duration regex
covers `ms` and `s`; the property lists above are the whole contract. Five groups stay out on
purpose:

- Sizing — `width`, `height`, `min-*`, `max-*`: only shell dimensions have tokens, and the 360px
  no-horizontal-scroll requirement ([layout.md](layout.md)) needs literal min/max widths.
- `transform` and `translate`: motion and offset values are viewport- or element-relative
  geometry, not a spacing scale; there is no numeric token for `translateY(-2px)`.
- `letter-spacing`, `word-spacing`, `text-indent`: the type scale has size and line-height tokens
  but no tracking token, so any numeric value here has no token to reference.
- `border-spacing`: no token exists for table cell gaps, and no component rule has yet chosen one.
- Timing functions — `cubic-bezier(...)`, `steps(...)`, `ease`/`linear`: `--eg-ease` exists, but
  none of these carries a unit a literal scanner can key on, and the keywords are legitimate CSS.
  Using the token here is a review convention, not a lint verdict.

Adding a token for any of these is the prerequisite for checking them, but a token alone is not
sufficient: the rule matches literals by unit, which is why the motion durations are reported and
`--eg-ease` is not.

**CSS system colour keywords pass.** `Canvas`, `CanvasText`, `ButtonText`, `LinkText` and the rest
are not CSS named colours, so they never match the named-colour check. That is intended: a system
colour resolves to the user's own OS colour, carries no brand value, and is the recommended way to
keep text and control boundaries readable under `forced-colors: active` (the WCAG 2.2 system-colour
/ non-text contrast guidance). A spec case proves `color: CanvasText`, `background-color: Canvas`,
`border-color: ButtonText` and `outline-color: LinkText` all pass.

**Allowed everywhere:** `var(--*)` references (a local alias such as
`--local-gap: var(--eg-space-4)` is safe, because the literal check runs on each declaration and a
`var()` reference never matches), `0`, `0px`/`0rem`, `0s`/`0ms`, unitless numbers
(`line-height: 1.5`, `font-weight: 600`), percentages (`100%`), `transparent`, `currentcolor`,
`inherit`, `initial`, `unset`, `revert`, `revert-layer`, `none` and `auto`, quoted strings,
`url(...)` payloads, and `color-mix()` whose operands contain no literal (its operands are checked,
so `color-mix(in srgb, #fff 10%, red)` fails twice).

**Media-query exception.** `@media`, `@container` and `@supports` PRELUDES are never checked,
because CSS cannot read a custom property inside a condition — `--eg-bp-tablet: 768px` and
`--eg-bp-desktop: 1280px` are documented mirrors only ([layout.md](layout.md), "Breakpoints and
navigation"). Declarations INSIDE those at-rules are still checked, so
`@media (min-width: 768px) { .a { padding: var(--eg-space-4); } }` passes while the same block with
`padding: 16px` fails.

**The `tokens.css` exemption** is a config-level `ignores` on the CSS rule: raw values are allowed
in `apps/web/src/styles/tokens.css` only, which is where the light and dark palettes live.

**Inline `styles: [...]` in an `@Component`** is checked by the second rule, which reads the
declaration text out of the string or template literal:

- A plain string entry and a template entry without interpolation are scanned exactly like a
  stylesheet, so every literal above fails at its real line and column in the `.ts` file.
- An interpolation immediately followed by a raw unit — `${space}px`, `${space}rem`,
  `${space}em`, `${duration}ms`, `${duration}s` — fails with `rawLength` or `rawDuration` and the
  value `${space}px` **in a checked property only**: the interpolation holds a JavaScript number,
  so the unit is what turns it into a literal length or time.
  `padding: ${space}px`, `--local-gap: ${space}rem` and `transition-duration: ${duration}ms` fail;
  `padding: ${space}%`, `gap: ${token}`, and `width: ${size}px` /
  `transform: translateY(${offset}px)` pass, the last two for the same reason their static forms
  pass — sizing and transform are outside the contract.
- `${...}` expression source is masked before the entry is read as a stylesheet, so a JavaScript
  expression is never itself scanned for colours, lengths or durations.
- `styles: [NAME]` where `NAME` is a module-level `const NAME = '...'` or `` const NAME = `...` ``
  in the same file is resolved and checked, so hoisting a stylesheet into a const does not bypass
  the rule.

**Known limitations.** These are the shapes the rules deliberately do not claim to cover:

- Template `style="…"` attributes and `[style.x]` bindings: template input rather than a
  stylesheet literal, and neither rule has a template AST.
- A style string built elsewhere or imported (`styles: [sharedStyles]` from another module,
  `styles: [buildStyles()]`, or a `let`/`var` binding): only a module-level `const` string or
  template literal in the same file is resolved; chasing other shapes would need whole-program
  analysis and would guess at runtime values.
- The `@core.Component({...})` namespace form: the inline rule matches a bare `Component`
  identifier callee, so `core.Component` is not checked. Import `Component` directly (the project
  convention) or the styles in that metadata are unchecked.
- `styles` inside a `*.spec.ts`: the `apps/web/**/*.ts` config block ignores specs, so a test host
  component's inline styles are not linted.

**Disposition of each limitation (S62 send-back on PR #138).** Nothing above is left unowned:

| Limitation or review nit | Disposition |
| --- | --- |
| Template `style="…"` attributes and `[style.x]` bindings | Handed to 09.1.1-T7 (plan `09.1.1-app-scaffold-and-build-acceptance-npm.md:181`, STATUS §6 row "09.1.1-T7 `style-tokens` coverage gaps") |
| Motion durations (`transition`/`animation`) | Closed in 09.1.1-T8: `rawDuration` now reports a non-zero `ms`/`s` time in the six duration properties, in every custom property and in an interpolated `${duration}ms`/`${duration}s` (this section). T7's hand-over note said the T8 patterns animate nothing, so the lint rule and this contract are the pattern the hand-over owed |
| `border-spacing` | Handed to 09.1.1-T7 (same note and row; the shared list table picks the gap) |
| Style string imported, built by a function, or held in `let`/`var` | Accepted by design, not handed over: resolving it needs whole-program analysis and would guess runtime values; code review is the net |
| `@core.Component` namespace form | Accepted by design, not handed over: the project convention imports `Component` directly |
| `styles` in `*.spec.ts` | Accepted by design, not handed over: test host components are never shipped |
| Sizing, `transform`/`translate`, `letter-spacing`/`word-spacing`/`text-indent` | Accepted by design, not handed over: no numeric token exists to replace them (see "Deliberately not checked") |
| Timing functions (`cubic-bezier(...)`, `steps(...)`, `ease`/`linear`) | Accepted by design, not handed over: `--eg-ease` is a review convention only, because a timing function has no unit for the literal scanner to key on and the keywords are legitimate CSS (see "Deliberately not checked") |
| CSS system colour keywords | Accepted by design, not handed over: they carry no brand value and are the forced-colors pattern |
| `.scss`/`.sass` files not matched | Accepted by design, not handed over: `apps/web/angular.json` sets `style: css` and no SCSS file exists |
| Review nit: one `const` used by two components reports twice at the const's location | Accepted by design, not handed over: duplicate message only, never a missed or false verdict |
| Review nit: `${n}px` inside a comment within a template literal can be reported | Accepted by design, not handed over: an extra report on an unrealistic input, fixed by deleting the comment |

A new component foreground/background pair still has to be added to `contrast.py` and regenerated
into the table above — the lint rule checks spelling, not contrast.
