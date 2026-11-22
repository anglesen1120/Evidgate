# Canonical v3 fixture

Use these records throughout the 00–47 design walkthrough. All timestamps are UTC. A URL, breadcrumb, heading, evidence link and confirmation must name the same record. A historical snapshot is not a current grant or decision.

## Workspace and people

| Field | Canonical value | Presentation |
| --- | --- | --- |
| Organization | Lotus Commerce | Organization-wide views say **Organization settings**; the selected project does not narrow an organization-wide operation. |
| Project | `storefront-web` · `SHOP` · `p-storefront` | Use the same name/ID in the selector, CLI examples, requests, destructive confirmations and breadcrumbs. |
| Brand | Evidgate · Quality workspace | VI: **Không gian chất lượng**. No alternate tenant, product or project alias. |
| Signed in as | Minh Trần · Test maintainer · MT | Default global session. Only the account control in the topbar identifies the signed-in person. |
| Authored by | Linh Nguyễn · QA engineer · LN | Authored the case/repair. Attribution is not authentication; never sign an edit as Linh while displaying Minh. |
| Assigned reviewer | Minh Trần · Test maintainer | An assignment is not an approval. An actual decision separately names reviewer, timestamp and candidate hash. |
| Administrative actor | Hà Lê · Tenant administrator · HL | A recorded admin event names Hà without changing the session. For a deliberate persona demo, explicitly label “Preview as Hà Lê · Tenant administrator” before the role-specific action, change the account display too, and label the way back. Opening Settings is not a switch. |

## Runs and release bindings

| Record / frozen evidence | Completeness | Observed outcome | Stored decision | Scope / interpretation |
| --- | --- | --- | --- | --- |
| `run-1842 / ev-3` | Incomplete; shard-3 missing | `has_failures` | `FAIL` | Official CI; `sr-27`, `mf-7d2c…91ab`, `pr-12`. Missing evidence does not erase a known failure. Inspect ev-3 even when a newer revision exists. |
| `run-1842 / ev-4` | Complete after reprocessing | `has_failures` | Keep ev-3's decision pinned to ev-3; do not copy it to a new evaluation | Evidence advanced; no new execution and no retroactive PASS. |
| `run-1843` | Collecting | No stored outcome | Pending; no decision | Local run, not the retry of 1842. Do not invent a frozen revision. |
| `run-1844 / ev-3 → ev-4` | Complete | `all_passed` | `PASS` at the web binding `ev-4` | Earlier register snapshot showed ev-3; release detail opens ev-4. `web`, `sha256:a72c…09de`, evidence fresh. |
| `run-1845 / ev-3` | Complete | `has_failures` | `FAIL` | Failed-subset rerun of 1842, not full regression. Separate release comparison/binding example; **not** a worker binding in `rd-14`. |
| `run-1837 / ev-2` | Complete | `all_passed` | `PASS` | `api`, `sha256:b91f…17bc`; evidence predates recorded build. Stale PASS does not establish release readiness. |
| `rel-2026.10.13 / rd-14` | 2 of 3 components bound | No single test outcome | `INCONCLUSIVE` release assessment | Web → 1844/ev-4; API → 1837/ev-2; worker `sha256:c44a…2a78` → **no eligible run**. Never substitute 1842, 1845 or a sandbox result for worker. |

| Producer at `run-1842 / ev-3` | Reported | Accepted | Expected | Meaning |
| --- | ---: | ---: | ---: | --- |
| shard-2 | 48 | 47 | 48 | 48 reported; 47 accepted of 48 expected. “Reported 47” is incorrect. |
| shard-3 | — | — | Expected producer absent | Missing, not an observed zero-pass result. |

## Record identity and artifacts

| Stable record | Preserve across pages |
| --- | --- |
| `case-checkout-17` / `SHOP-TC-0042 v5` | Checkout confirms order. `inc-91` = `product_defect`; `run-1842 / checkout-vi-chromium / att-1842-01` (#1). Raw: `POST /orders → HTTP 500`, request `req-901`; normalized: “Expected order confirmation; received HTTP 500.” Neither receipt-count assertions nor a locator timeout describe this attempt. |
| `att-1842-02` | Same execution and run, attempt #2 passed. A later successful retry does not replace #1, its screenshot or its decision. |
| `art-shot-92 / ev-3` | Official CI screenshot, `image/png`, 248 KiB, `sha256:ad72…f18b`; context `run-1842`, original checkout attempt. The numeric suffix **does not** associate this artifact with `inc-92`. |
| `case-address-8` / `SHOP-TC-0043` | `inc-92`: required delivery address field not found before timeout; `test_drift`. Distinct failed execution under 1842, not `att-1842-01`. History follows the address case, not the checkout case. |
| `cand-73 / ch-8af2…3e20` | For `inc-92` only. Revision 7, base `9af317c`; before `getByTestId('delivery-address-required')`; after `getByRole('textbox', { name: 'Required delivery address' })`. Linh authored; Minh reviews. |
| Shadow validation for `cand-73` | 24/24 passed **in sandbox only**, for that hash/base. Proposal validation: Validated. Human approval: Awaiting review in the default snapshot. Original run: Incomplete / has_failures / stored FAIL. Sandbox results are neither a release decision nor official CI evidence. |
| `cand-73 / ch-91d4…63fc` | Alternative conflict state, revision 8. Do not reuse revision 7's validation, approvals or counts for the replacement hash. |
| Other queue records | `cand-77 / ch-27ca…512e`: cart wait, 8/8 sandbox samples. `cand-74 / ch-51de…094f` and `cand-75 / ch-3c42…8591`: no shadow samples; rate unavailable, approval not enabled. Bulk confirmation lists selected IDs and hashes only. |
| `ev-s1` | Studio sandbox artifact. Link to its own viewer context, never `art-shot-92 / ev-3`. If unavailable, display “Sandbox artifact ev-s1 unavailable” and preserve the source label. |
| `case-search-4` | Search case. History is by stable case ID; never substitute `case-checkout-17` or `case-address-8`. |

## Authoring and AI policy

| Record | Canonical state |
| --- | --- |
| `SHOP-TC-0042` | v4 **Approved**, immutable; v5 **Drafting**. Do not present v5 as approved or allow in-place edits to v4. |
| `SHOP-TC-0051` | v2 **Approved**, immutable, seven steps; step 7 expected result: **Discount field is visible**. v3 **Awaiting review**, not a replacement for approved v2. |
| Organization AI consent | **Off** as of 2026-10-23 10:00 UTC; generation is blocked. Opt-in **On** is an explicitly labelled alternative state, not the default or an implied grant. |
| AI settings versions | `ais-2` was in force on 2026-10-21. `ais-3` was created 2026-10-23 09:00 UTC; do not apply it retroactively or treat its creation as organization consent. |

## Event ledger (UTC)

Unless marked otherwise the date is **2026-10-13**. The selected UI state may be an earlier snapshot; display “As of … UTC” where later events would otherwise make it ambiguous.

| UTC | Record | Event |
| --- | --- | --- |
| 2026-10-12 08:40:00 | `SHOP-TC-0042 v5` | Drafting revision authored by Linh before run creation. The run preserves its captured version; the approved immutable revision is v4. |
| 09:42:00 / 09:42:01 | `run-1842` | Created against suite `sr-27`, then queued. |
| 09:42:03 | `att-1842-01` | Original checkout execution starts. |
| 09:42:04–09:42:06 | `inc-91` | Place order → HTTP 500 → assertion fails; first seen 09:42:06, not 23 Oct. |
| 09:42:06.718 | `art-shot-92` | Capture after the failed attempt started and before freeze; not 09:41:20.718. |
| 09:43:10 | `inc-92` | Separate delivery-address locator timeout captured during this run. Do not report the later review time as original capture time. |
| 09:44:00 | `run-1842 / ev-1` | Collecting snapshot. |
| 09:45:00–09:45:02 | `att-1842-02` | Checkout retry within 1842 passes (1,630 ms). Same case/revision; immutable failed first attempt remains inspectable. |
| 09:46:12 | `run-1842 / ev-2` | Earlier frozen snapshot; pending trace, screenshot not yet attached. |
| 09:47:00 | `run-1842 / ev-3` | Freeze official snapshot; screenshot attached, shard-3 missing. Link evidence and decision to this revision, not “latest”. |
| 09:49:20 | `run-1842 / ev-4` | Later reprocessing completes collection. No replacement execution. |
| 09:50:00 / 09:58:00 | `cand-73`, rev 7 | Proposal created / 24 sandbox validations complete. Never date shadow validation before the underlying failure. |
| As of 09:59:00 | Screens 12–14 | Default queue/detail: validated, awaiting human review. Age is computed from proposal creation (9 minutes here); do not carry the trial's 29-hour label onto this record. Over-SLA specimens must name a genuinely overdue record or a separately dated state. |
| As of 10:00:00 | Approval preview | Explicit, alternative approve/reject scenario. Persisted review would name Minh and the exact rev-7 hash. Not proof the default pending case was approved. |
| As of 10:15:00 | `rel-2026.10.13 / rd-14` | Release-time binding snapshot. Pinned decisions above do not follow a later register revision. |
| 10:42:00 → 11:00:00 | `sg-41`, Support-07 | Request at 10:42. Approved-grant preview **as of 10:50 UTC**: artifact metadata only for `storefront-web`. At/after 11:00 UTC it is expired; a request alone is not an active grant. |
| As of 2026-10-23 10:00 | Later triage/catalog snapshots | Label later classification, confirmation or project edits with their own timestamps. The original run, case version and capture dates do not move; `sg-41` is expired. |

## Glossary

| English | Tiếng Việt |
| --- | --- |
| Quality workspace | Không gian chất lượng |
| Evidence completeness | Mức độ đầy đủ của bằng chứng |
| Test outcome | Kết quả kiểm thử |
| Stored run decision | Quyết định đã lưu của lượt chạy |
| Release assessment | Đánh giá phát hành |
| Original failure | Lỗi gốc |
| Incomplete | Chưa đầy đủ |
| Collecting | Đang thu thập |
| Missing evidence | Thiếu bằng chứng |
| Awaiting review | Chờ xét duyệt |
| Over SLA | Quá hạn xét duyệt |
| Validated | Đã kiểm chứng |
| Approved | Đã phê duyệt |
| Sandbox only | Chỉ trong môi trường mô phỏng |
| Design review controls | Trạng thái bản mẫu |
| Prototype state | Trạng thái bản mẫu |
| Signed in as | Đang đăng nhập |
| Authored by | Tác giả |
| Assigned reviewer | Người được phân công duyệt |
| As of | Tính đến |
| Organization settings | Cài đặt tổ chức |
| Quality overview | Tổng quan chất lượng |
| Case history | Lịch sử ca kiểm thử |
| Knowledge lineage | Nguồn gốc tri thức |
| Required delivery address field not found before timeout. | Không tìm thấy trường địa chỉ giao hàng bắt buộc trong thời gian chờ. |

Keep IDs, hashes, wire enums (`has_failures`, `all_passed`) and stored verdicts (`PASS`, `FAIL`, `INCONCLUSIVE`) literal. Translate their explanatory labels, not the record keys.
