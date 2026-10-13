# S44 Web UI design runs

Project: `Evidgate — Web UI` (`evidgate-web-ui-0026`), skill `frontend-design`, model `gpt-6-luna`. Agent per page in the table (amr = OpenDesign Cloud; codex = Codex CLI bundled in OpenDesign).

Runtime switch: from page 06 (06-decision-waivers.md) on, agent `codex` after amr error: `AMR_INSUFFICIENT_BALANCE: AMR Cloud reported insufficient balance for this model. Top up your AMR balance at https://open-design.ai/cloud/dashboard?source=open_design, then retry this run. action=recharge`.

## Current runs

| file | run id | status | output path | agent/model | retries | notes |
|---|---|---|---|---|---|---|
| 01-app-shell.md | be6eed3b-eacb-44aa-bed1-ac3ded61cbe2 | saved | docs/design/web-ui/output/01-app-shell/ | amr/gpt-6-luna | 0 | PILOT (amr); events log shows model gpt-6-luna; keyword regex check only: light/dark, 403/412, loading/empty present, not visually verified |
| 02-login.md | 2df19733-f5c0-4cc8-933a-8cbd0f5ed2aa | saved | docs/design/web-ui/output/02-login/ | codex / gpt-6-luna | 0 | [amr failed x2: HTTP 400 BadRequest on opencode session continue] re-run on codex per owner; 02: amr failed twice (HTTP 400 on opencode session), re-run on codex;  |
| 03-run-list.md | 2d5e72ac-0c8c-4eaa-ad8a-bf92b9774e43 | saved | docs/design/web-ui/output/03-run-list/ | codex / gpt-6-luna | 0 | [first attempt(s) misattributed/failed on amr: amr run produced evidgate-run-detail.html instead of this page or failed with HTTP 400 / 600s stall] re-run on codex per owner;  |
| 04-run-detail.md | 8de33388-a241-4e71-815f-d387d08c090e | saved | docs/design/web-ui/output/04-run-detail/ | codex / gpt-6-luna | 0 | [first attempt(s) misattributed/failed on amr: amr run produced evidgate-run-detail.html instead of this page or failed with HTTP 400 / 600s stall] re-run on codex per owner; shares evidgate-run-detail.html with another page;  |
| 05-evidence-panel.md | ac8815c3-06e4-4275-aad8-f56e1fd8e502 | saved | docs/design/web-ui/output/05-evidence-panel/ | codex / gpt-6-luna | 1 | [first attempt(s) misattributed/failed on amr: amr run produced evidgate-run-detail.html instead of this page or failed with HTTP 400 / 600s stall] re-run on codex per owner; retry after failed:AGENT_EXECUTION_FAILED Agent stalled without emitting any new output for 600s. The model or CLI likely hung while generating. Phase details: spawned agent codex; stdout arrived: yes; last agent event: spawn |
| 06-decision-waivers.md | d2cafd16-6f65-479a-8ef9-120eaf39e552 | saved | docs/design/web-ui/output/06-decision-waivers/ | codex / gpt-6-luna | 0 | QUOTA SWITCH amr->codex, error: failed:AMR_INSUFFICIENT_BALANCE AMR Cloud reported insufficient balance for this model. Top up your AMR balance at https://open-design.ai/cloud/dashboard?source=open_design, then retry this run. action=recharge;  |
| 07-report-comparison.md | d56c86e3-7a29-4480-aa40-dc672d8efbc3 | saved | docs/design/web-ui/output/07-report-comparison/ | amr / gpt-6-luna | 0 |  |
| 08-failure-inbox.md | c2cd3d81-93c1-4d15-b4c0-54b7de0f2d7c | saved | docs/design/web-ui/output/08-failure-inbox/ | codex / gpt-6-luna | 1 | retry after failed:AMR_INSUFFICIENT_BALANCE AMR Cloud reported insufficient balance for this model. Top up your AMR balance at https://open-design.ai/cloud/dashboard?source=open_design, then retry this run. action=recharge; file written by an earlier 08 attempt (run 87716a6d on amr, failed insufficient balance mid-way) and kept;  |
| 09-failure-detail-triage.md | adde3bbf-46f9-42f5-8f7f-4e09bdbb1bf6 | saved | docs/design/web-ui/output/09-failure-detail-triage/ | codex / gpt-6-luna | 1 | retry after failed:AMR_INSUFFICIENT_BALANCE AMR Cloud reported insufficient balance for this model. Top up your AMR balance at https://open-design.ai/cloud/dashboard?source=open_design, then retry this run. action=recharge;  |
| 10-investigation-history.md | 388b7638-8b65-4f7b-bf14-c526be9bbffc | saved | docs/design/web-ui/output/10-investigation-history/ | codex / gpt-6-luna | 0 |  |
| 11-artifact-viewer.md | 77644373-9e26-4deb-8f5f-025f540e92f0 | saved | docs/design/web-ui/output/11-artifact-viewer/ | codex / gpt-6-luna | 0 |  |
| 12-healing-queue.md | 14b0751a-55f9-43b5-96db-00a24d8f39b6 | saved | docs/design/web-ui/output/12-healing-queue/ | codex / gpt-6-luna | 0 |  |
| 13-healing-shadow-detail.md | a9da19e0-3c49-4f8a-845a-baabe2f6fd45 | saved | docs/design/web-ui/output/13-healing-shadow-detail/ | codex / gpt-6-luna | 0 | shares evidgate-candidate-detail.html with another page;  |
| 14-healing-approval.md | a90b1568-8fa4-458e-9f99-93964314a367 | saved | docs/design/web-ui/output/14-healing-approval/ | codex / gpt-6-luna | 0 | shares evidgate-candidate-detail.html with another page; 14 shares the candidate-detail page with 13;  |
| 15-knowledge-revocation.md | 1a3fa091-fd68-4d6c-8f41-5b4bccbf747c | saved | docs/design/web-ui/output/15-knowledge-revocation/ | codex / gpt-6-luna | 0 |  |
| 16-healing-availability.md | 25c4ebc1-8825-496b-84dc-1ea6c8ca4c21 | saved | docs/design/web-ui/output/16-healing-availability/ | codex / gpt-6-luna | 0 |  |
| 17-studio-template-gallery.md | aa372ecf-16f9-4239-8dc6-c5ce78ec831f | saved | docs/design/web-ui/output/17-studio-template-gallery/ | codex / gpt-6-luna | 0 | shares evidgate-test-studio.html with another page;  |
| 18-studio-draft-editor.md | 11d56b68-e291-4740-a633-96bf81325c93 | saved | docs/design/web-ui/output/18-studio-draft-editor/ | codex / gpt-6-luna | 0 | shares evidgate-test-studio.html with another page; 18 shares the test-studio page with 17;  |
| 19-studio-review-merge.md | 00987251-e76b-455e-a565-0759463f3c25 | saved | docs/design/web-ui/output/19-studio-review-merge/ | codex / gpt-6-luna | 0 |  |
| 20-suite-composition.md | ac21ed3f-fa4f-4dbb-8b5d-21ade571d9d2 | saved | docs/design/web-ui/output/20-suite-composition/ | codex / gpt-6-luna | 0 |  |
| 21-release-view.md | 4fbf1630-c880-464c-87da-3091ff51c6a1 | saved | docs/design/web-ui/output/21-release-view/ | codex / gpt-6-luna | 0 |  |
| 22-quality-overview.md | 051b31f2-156e-4410-8772-82e68104aae1 | saved | docs/design/web-ui/output/22-quality-overview/ | codex / gpt-6-luna | 0 |  |
| 23-onboarding-wizard.md | 2fdfe57e-6137-4e3f-bf63-4b9f587d5f85 | saved | docs/design/web-ui/output/23-onboarding-wizard/ | codex / gpt-6-luna | 0 |  |
| 24-project-connection.md | f8954abf-38ac-4631-a399-2462a9b1b3d8 | saved | docs/design/web-ui/output/24-project-connection/ | codex / gpt-6-luna | 0 |  |
| 25-test-catalog.md | e4a8183a-057c-4114-b790-599ebc09b478 | saved | docs/design/web-ui/output/25-test-catalog/ | codex / gpt-6-luna | 0 |  |
| 26-mapping-resolution.md | 17cf5f80-2002-4a50-9922-e41e19f449b3 | saved | docs/design/web-ui/output/26-mapping-resolution/ | codex / gpt-6-luna | 1 | retry after failed:AGENT_EXECUTION_FAILED Agent stalled without emitting any new output for 600s. The model or CLI likely hung while generating. Phase details: spawned agent codex; stdout arrived: yes; last agent event: spawn pending; largest tool result observed: 0 chars. Retry the turn, pick a different model, or start a new conversation  action=retry;  |
| 27-settings-shell.md | a5bac134-a513-47fd-ba22-98f1302e941e | saved | docs/design/web-ui/output/27-settings-shell/ | codex / gpt-6-luna | 0 |  |
| 28-members-roles.md | ba4ed7ce-301c-4b32-8150-cb22f9513231 | saved | docs/design/web-ui/output/28-members-roles/ | codex / gpt-6-luna | 0 |  |
| 29-secret-references.md | 963a6703-1ca0-4185-a7e1-cf83163f8b7e | saved | docs/design/web-ui/output/29-secret-references/ | codex / gpt-6-luna | 0 |  |
| 30-policy-revisions.md | 612be3d4-75c6-4242-91b3-885e9d232c75 | saved | docs/design/web-ui/output/30-policy-revisions/ | codex / gpt-6-luna | 0 |  |
| 31-quota-retention.md | a1a96ba7-f51b-4a5b-a708-7a5e9e3f4ff4 | saved | docs/design/web-ui/output/31-quota-retention/ | codex / gpt-6-luna | 0 |  |
| 32-audit-log.md | 724182d7-1d97-453b-bfaa-b122eec1de44 | saved | docs/design/web-ui/output/32-audit-log/ | codex / gpt-6-luna | 0 |  |
| 33-security-events.md | 9c6eb2b9-7ab5-46af-b9c8-6ee7b2df1ba4 | saved | docs/design/web-ui/output/33-security-events/ | codex / gpt-6-luna | 0 |  |
| 34-agent-pools.md | 9c9a2d68-797a-4a95-b811-533c0235ad7a | saved | docs/design/web-ui/output/34-agent-pools/ | codex / gpt-6-luna | 0 |  |
| 35-integrations-webhooks.md | d388b3fa-452b-4a63-8793-a5fa9b546599 | saved | docs/design/web-ui/output/35-integrations-webhooks/ | codex / gpt-6-luna | 0 |  |
| 36-support-access.md | b1038594-ef79-4504-89c7-dafc4c974b9c | saved | docs/design/web-ui/output/36-support-access/ | codex / gpt-6-luna | 0 |  |
| 37-api-tokens.md | f4d1ecd5-734d-49e9-950b-500b4bbebee2 | saved | docs/design/web-ui/output/37-api-tokens/ | codex / gpt-6-luna | 0 |  |
| 38-deploy-targets.md | a2994de6-150f-4391-aca2-d189cb6c9481 | saved | docs/design/web-ui/output/38-deploy-targets/ | codex / gpt-6-luna | 0 |  |
| 39-locale-layout-review.md | c8dd32eb-518b-48ba-a2f0-284af3a04516 | saved | docs/design/web-ui/output/39-locale-layout-review/ | codex / gpt-6-luna | 0 |  |

## Superseded codex runs (first runtime, before owner switch to amr)

| file | run id | note |
|---|---|---|
| 01-app-shell.md | 7d160b9d-1da1-428c-82a2-d23b761006b5 |  |
| 02-login.md | 79bddf0f-1547-4249-be9a-ec003de2bdb4 | codex run had already completed (succeeded) before switch; output not saved; superseded: runtime switch |
| 03-run-list.md | 31e80045-c7dc-4e97-b730-34f92453e67e | codex run had already completed (succeeded) before switch; output not saved; superseded: runtime switch |
| 04-run-detail.md | 66632bc5-5c76-44e0-93ee-1c4b1bcf689d | codex run had already completed (succeeded) before switch; output not saved; superseded: runtime switch |

## Quality gaps (regex check on saved HTML; not visual)



## Link pass and CSS centralization (post-run)

- Link check script (all local `href`s in the 39 entry pages, plus fragment ids): **355 local hrefs, 0 broken files, 0 missing fragments**.
- Nav check: **228 anchors inside nav/aside blocks, 1 not pointing to a page** (`21` `#decision` = in-page fixture link `pr-12`). Main nav, settings sub-nav (27 JS ROUTES to 28/29/31/32/33/35/36/37/38), run row to 04, incident row to 09, candidate row to 13, studio and wizard steps are linked; map in `NAV.md`, entry list in `index.html`.
- Unwired by design (no such element in the page): 05 back-to-run, 09/14/15 healing links, 10 incident, 38 policy-pin anchor (JS toast), 39 specimen tabs, 30 `#linkedRun`.
- Single stylesheet: all inline `<style>` blocks moved to `assets/evidgate.css` (842 KB), each page scoped under `html[data-page="NN"]` (pages 05, 14, 18 reuse the CSS of 04, 13, 17). Pages link `../assets/evidgate.css`. Pixel check with system Chrome, 1280x900 full page, light+dark, before vs after: 38 of 39 pages identical; page 13 light differed once and matched on two reruns (non-deterministic element, not CSS).
- Not done: merging tokens/components across pages into shared (unscoped) rules. The pages were generated independently, so same-named classes differ; hoisting needs a design-system pass.
- Codex pilot of page 01 is kept in `01-app-shell.codex/` with inline CSS (comparison only).

## UI review fixes (coordinator review S44, hand-edited; no OpenDesign re-run)

Files: `assets/evidgate.css` (shared block appended, sections A-F), `assets/evidgate.js` (new, shared), the 39 entry pages (one `<script src="../assets/evidgate.js">` line after the stylesheet link), plus two script bug fixes in `03-run-list` and `35-integrations-webhooks`.

- Item 1 (page sideways scroll at 768): the `.visually-hidden` spans inside table wrappers escaped them because the wrapper was not a containing block (29, 25: `position: relative` on `[class*="table-wrap|scroll|shell"]`); 38 (`min-width: 0` on grid children, wrapping tab row and release filter) and 39 (`.metric-row` columns).
- Items 2, 3 (button labels on one line): `white-space: nowrap` for button, `.button`, `.btn`, `.text-button`, `[role=tab]` and the like; action rows wrap instead (`flex-wrap`); 28 action column stacks.
- Item 4 (01 at 768): account name visually hidden below 1280 px, `.account-trigger` sized from its content, topbar wraps.
- Item 5 (15 footer, 32 chips): stacked label/value pairs are block elements; chips nowrap.
- Item 6 (words never break inside): badge/pill/tag/status/chip/marker classes are `nowrap` + `flex-shrink: 0` (circles sized from content); short tokens with `- . : / – …` (ids, timestamps, hashes, hyphenated words, 2-24 chars) are wrapped by `assets/evidgate.js` in `span.nw` (nowrap), including after state/locale re-renders.
- Menu toggle (owner request): `#menuToggle` as the first item of every top bar that has an `aside.sidebar` (37 pages; 02 login and 39 locale review have no menu). Hides the sidebar, releases grid/margin/padding/left offsets measured from the real layout, remembers the choice in `localStorage` (`evidgate.menuCollapsed`), labels follow `html[lang]` (Hide menu / Ẩn menu), `aria-expanded` and `aria-controls` set, focus returns to the first menu link when reopened. **Superseded by S49** (below): the toggle now collapses to an icon rail instead of hiding the menu.
- Page script bugs found by the toggle test and fixed: 03 `stateOptionLabels is not defined` (removed the dead statement, review-only option labels stay English), 35 `classList.add('')` in `renderReview` (guarded).

Verification (system Chrome, 39 pages, 1440 and 768, en and vi, light and dark, every option of the state switcher):

| Check | Before | After |
|---|---|---|
| Words under 25 chars split over two lines (Range + `getClientRects`), all page x width x lang x theme x state combinations | 3689 (103 distinct word/element pairs, 34 pages) | 0 |
| Pages scrolling sideways at 768 / 1440 (en, vi) | 29, 38, 25, 39 (+ 01/04/05 after the toggle was first added) | 0 |
| Button labels on 2+ lines (text-node line count) | many | 0 |
| Menu toggle: hides sidebar, content reflows, persists after reload, reopens (37 pages x 2 widths) | n/a | 71/74 pass, 3 notes below |
| Local refs / broken / missing fragments | | 471 real refs, 0 broken, 0 missing fragments (the only non-file hit is the JS template string in 27) |
| Inline `<style>` blocks | 0 | 0; 39/39 link the shared CSS and JS |

Notes: page 39 has no state switcher (only its default state was scanned). Toggle test flags on 10, 24, 25 at 1440 (content starts at x=142-184 after collapsing) are the pages' own centred `max-width` content, not a leftover sidebar offset. The detector exempts nothing; long-content states (20 `long`) pass because `.variant` no longer uses `overflow-wrap: anywhere`.

## UI review fixes, round 2 (coordinator re-verify)

- 11 artifact viewer, state `long`: the round-1 `.mono { overflow-wrap: normal }` made the 71-char `#checksum` unbreakable and pushed `#copyChecksum` 184 px off screen. Long values (`#checksum`, `[data-full-value]`, `[class*=hash]`, `[class*=digest]`) are carved out and keep `overflow-wrap: anywhere`; the hash line is `min-width: 0` with a non-shrinking copy button.
- 13/14 at 768: two causes. (a) The attribute selector `[class*="status"]` also matched `section.review-actions.status-disabled` (class set by the page script) and gave a whole panel `min-width: max-content`; the shared badge/pill/chip/status/tag/marker attribute selectors are now limited to inline tags (`span, small, b, strong, i, em, a, td, label, time`). (b) `.review-actions` used `1fr 1fr` tracks (min-content floor); now `minmax(0,1fr)` columns, one column below 900 px; `.decision-buttons` wrap and stack full width below 900 px, the long Vietnamese labels may wrap between words inside the button, never inside a word.
- Sweep (system Chrome, 39 pages, 1440 and 768, en and vi, light and dark, every option of the state switcher): split words under 25 chars **0** (round 1 baseline 3689); `documentElement/body scrollWidth > innerWidth` combinations **0** (round-2 repro before the fix: 11 = 4, 13 = 48, 14 = 48); buttons with a label on 2+ lines (text-node line count, 1440/768 en/vi) **0**; page script errors **0**; menu toggle 71/74 (the 3 notes are centred `max-width` layouts); 471 real local refs, 0 broken, 0 missing fragments.


## S49 shared sidebar and collapsible icon rail (owner review 2026-10-14; hand-edited, no OpenDesign re-run)

Owner findings: (1) "Hide menu" hid the sidebar instead of shrinking it to an icon rail; (2) the 37 pages with a sidebar carried 35 different hand-written menus (different items, order, labels, languages and targets for the same label).

- One information architecture, 6 groups, 25 items (table in `NAV.md`, coordinator-approved as PANEL-CHOICE S49 IA). Detail pages highlight their parent item (rule per page in `NAV.md`; 11 artifact viewer highlights Runs by coordinator ruling). Brand mark links to 01.
- One source of truth: the nav array, vi/en labels, icons, brand/scope/account blocks live only in `assets/evidgate.js`; every page has the mount `<aside class="eg-nav" id="evidgateSidebar" data-eg-nav>` and `<html data-nav="<item id>">`. The old per-page sidebar markup is gone (37 pages) and the rules whose selectors all use the `.sidebar` class were removed from `assets/evidgate.css`. Shared styles are one commented block at the end of `evidgate.css` (`.eg-nav`, `--eg-nav-w`, shell/main normalisation so every page layout follows the sidebar width).
- Toggle (`#menuToggle`, first item of the top bar): expanded 224 px / collapsed rail 68 px; rail keeps every item as an icon with active mark, tooltip on hover and keyboard focus (`role=tooltip`), accessible names, brand mark and account avatar; label/`aria-expanded`/`aria-label` follow vi/en; state in `localStorage` `evidgate.menuCollapsed`, default rail below 1280 px; below 768 px the sidebar is a drawer (focus trap, Escape, scrim click, `aria-expanded`). Nav area scrolls inside the sidebar, the account block stays pinned.
- Page script fixes found by the sweep (page scripts, pre-existing): 01 old mobile-nav handlers removed (scope switcher kept); 16 `querySelector('.sidebar')` aria-label line removed; 27 guard for the removed `#org-switch` button; 39 dialog created before its listeners; 12 and 33 null/`forEach` guards in state renderers; 25 `$('[data-id=...]')` calls used `getElementById` (6 scenarios), 26 `state-guard` `.row-actions` guard.
- 25 test catalog (owner screenshot): fixed table column widths left the Owner column (134 px) too narrow for select + Save, so Save overlapped the Rename history column and the History button. Owner column 12% -> 16%, select `flex:1 1 0; min-width:0`, buttons `flex:none`; other columns re-balanced to 100%.

Verification (system Chrome via Playwright harness `/tmp/s49-verify/verify.mjs`, server on port 8793):

| Check | Result |
|---|---|
| Sidebar signature (ordered label+href, brand + 25 items), 37 pages | 1 distinct signature in en, 1 in vi; 0 pages with wrong active item, 0 mixed-language labels |
| Click-through: every sidebar link on every page (37 x 26) | 962/962 land on 200 with the same signature and the expected active item |
| Collapse: 37 pages x 1440/768 x expanded/rail (rail 68 px, 224 px expanded, 25 icons reachable, tooltip on focus, active visible, no sideways scroll, content starts at sidebar edge) | 148/148 combinations; persistence across a page change OK; drawer < 768 (focus trap, Escape, scrim) 5/5; toggle labels vi/en 3/3 |
| Regression: 39 pages x 1440/768 x en/vi x light/dark x every state option: split words < 25 chars / 2-line button labels / scrollWidth > innerWidth / uncaught page errors | 0 / 0 / 0 / 0 |
| Local links broken | 0 |

Before the page-script fixes the same sweep reported 74 uncaught page errors on 12, 25, 26 and 33 (68 of them also present on the pre-S49 snapshot, which the S44 sweep had not exercised); now 0. Snapshot of the pre-S49 output: `/tmp/s49-precss/` (not in the repo).

Vietnamese nav wording checked by eye in `assets/evidgate.js` (e.g. Lượt chạy, Hộp thư lỗi, Chính sách phát hành, Bản phát hành); the sidebar landmark label (`Primary navigation` / `Điều hướng chính`) also follows `html[lang]`. Page 25 re-checked at 1193 px after the column fix: Save and History no longer overlap; the last column scrolls inside the table wrapper as designed.
