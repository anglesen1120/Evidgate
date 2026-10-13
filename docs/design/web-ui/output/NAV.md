# Navigation map (S44 link pass)

Paths are relative to `docs/design/web-ui/output/`. From a page in folder A use `../<folder>/<file>`.

## Page index

| # | page | entry |
|---|---|---|
| 01 | app-shell | `01-app-shell/evidgate-workspace-shell.html` |
| 02 | login | `02-login/evidgate-sign-in.html` |
| 03 | run-list | `03-run-list/evidgate-runs.html` |
| 04 | run-detail | `04-run-detail/evidgate-run-detail.html` |
| 05 | evidence-panel | `05-evidence-panel/evidgate-run-detail.html` |
| 06 | decision-waivers | `06-decision-waivers/evidgate-run-decision.html` |
| 07 | report-comparison | `07-report-comparison/evidgate-report-compare.html` |
| 08 | failure-inbox | `08-failure-inbox/evidgate-failure-inbox.html` |
| 09 | failure-detail-triage | `09-failure-detail-triage/evidgate-incident-detail.html` |
| 10 | investigation-history | `10-investigation-history/evidgate-case-history.html` |
| 11 | artifact-viewer | `11-artifact-viewer/evidgate-artifact-viewer.html` |
| 12 | healing-queue | `12-healing-queue/evidgate-healing-review.html` |
| 13 | healing-shadow-detail | `13-healing-shadow-detail/evidgate-candidate-detail.html` |
| 14 | healing-approval | `14-healing-approval/evidgate-candidate-detail.html` |
| 15 | knowledge-revocation | `15-knowledge-revocation/evidgate-knowledge-lineage.html` |
| 16 | healing-availability | `16-healing-availability/evidgate-healing-availability.html` |
| 17 | studio-template-gallery | `17-studio-template-gallery/evidgate-test-studio.html` |
| 18 | studio-draft-editor | `18-studio-draft-editor/evidgate-test-studio.html` |
| 19 | studio-review-merge | `19-studio-review-merge/evidgate-draft-review.html` |
| 20 | suite-composition | `20-suite-composition/evidgate-suite-composition.html` |
| 21 | release-view | `21-release-view/evidgate-release-view.html` |
| 22 | quality-overview | `22-quality-overview/evidgate-quality-overview.html` |
| 23 | onboarding-wizard | `23-onboarding-wizard/evidgate-onboarding.html` |
| 24 | project-connection | `24-project-connection/evidgate-project-overview.html` |
| 25 | test-catalog | `25-test-catalog/evidgate-test-catalog.html` |
| 26 | mapping-resolution | `26-mapping-resolution/evidgate-mapping-resolution.html` |
| 27 | settings-shell | `27-settings-shell/evidgate-settings.html` |
| 28 | members-roles | `28-members-roles/evidgate-members-roles.html` |
| 29 | secret-references | `29-secret-references/evidgate-secret-references.html` |
| 30 | policy-revisions | `30-policy-revisions/evidgate-policy-revisions.html` |
| 31 | quota-retention | `31-quota-retention/evidgate-usage-retention.html` |
| 32 | audit-log | `32-audit-log/evidgate-audit-log.html` |
| 33 | security-events | `33-security-events/evidgate-security-events.html` |
| 34 | agent-pools | `34-agent-pools/evidgate-agents-pools.html` |
| 35 | integrations-webhooks | `35-integrations-webhooks/evidgate-webhook-integrations.html` |
| 36 | support-access | `36-support-access/evidgate-support-access.html` |
| 37 | api-tokens | `37-api-tokens/evidgate-api-tokens.html` |
| 38 | deploy-targets | `38-deploy-targets/evidgate-deploy-targets.html` |
| 39 | locale-layout-review | `39-locale-layout-review/evidgate-localization-review.html` |

## Information architecture: one sidebar for all 37 pages (S49)

One shared sidebar is rendered by `assets/evidgate.js` from a single nav array (6 groups, 25 items, labels in vi and en) into the mount `<aside class="eg-nav" data-eg-nav>` on every page; each page names its item with `<html data-nav="<item id>">`. Order, labels, icons and hrefs are identical on every page; labels follow `html[lang]`. 02 login and 39 locale review have no sidebar by design. The brand mark links to 01 app shell (home). Below the nav: account block (static, no link). Expanded 224 px, collapsed icon rail 68 px (default at < 1280 px), state in `localStorage` key `evidgate.menuCollapsed`; below 768 px the same toggle opens a drawer.

| group (en / vi) | item id | en label | vi label | target page | path |
|---|---|---|---|---|---|
| Overview / Tổng quan | `overview` | Overview | Tổng quan | 24 project-connection | `../24-project-connection/evidgate-project-overview.html` |
|  | `quality` | Quality | Chất lượng | 22 quality-overview | `../22-quality-overview/evidgate-quality-overview.html` |
|  | `onboarding` | Onboarding | Bắt đầu | 23 onboarding-wizard | `../23-onboarding-wizard/evidgate-onboarding.html` |
| QA / Kiểm thử | `catalog` | Test catalog | Danh mục kiểm thử | 25 test-catalog | `../25-test-catalog/evidgate-test-catalog.html` |
|  | `mapping` | Mapping resolution | Ánh xạ kết quả | 26 mapping-resolution | `../26-mapping-resolution/evidgate-mapping-resolution.html` |
|  | `runs` | Runs | Lượt chạy | 03 run-list | `../03-run-list/evidgate-runs.html` |
|  | `failures` | Failure Inbox | Hộp thư lỗi | 08 failure-inbox | `../08-failure-inbox/evidgate-failure-inbox.html` |
| Healing / Sửa cơ chế kiểm thử | `healing` | Healing Review | Xem xét sửa lỗi | 12 healing-queue | `../12-healing-queue/evidgate-healing-review.html` |
|  | `knowledge` | Knowledge lineage | Tri thức và thu hồi | 15 knowledge-revocation | `../15-knowledge-revocation/evidgate-knowledge-lineage.html` |
|  | `availability` | Availability | Trạng thái sửa lỗi | 16 healing-availability | `../16-healing-availability/evidgate-healing-availability.html` |
| Test Studio (V2) / Test Studio V2 | `studio` | Test Studio | Test Studio | 17 studio-template-gallery | `../17-studio-template-gallery/evidgate-test-studio.html` |
|  | `suites` | Suite composition | Bộ kiểm thử | 20 suite-composition | `../20-suite-composition/evidgate-suite-composition.html` |
| Release / Phát hành | `releases` | Releases | Bản phát hành | 21 release-view | `../21-release-view/evidgate-release-view.html` |
|  | `policies` | Release policies | Chính sách phát hành | 30 policy-revisions | `../30-policy-revisions/evidgate-policy-revisions.html` |
|  | `deploy` | Deploy targets | Mục tiêu triển khai | 38 deploy-targets | `../38-deploy-targets/evidgate-deploy-targets.html` |
| Settings / Cài đặt | `settings` | General | Chung | 27 settings-shell | `../27-settings-shell/evidgate-settings.html` |
|  | `members` | Members & roles | Thành viên và vai trò | 28 members-roles | `../28-members-roles/evidgate-members-roles.html` |
|  | `tokens` | API tokens | Mã truy cập API | 37 api-tokens | `../37-api-tokens/evidgate-api-tokens.html` |
|  | `secrets` | Secret references | Tham chiếu bí mật | 29 secret-references | `../29-secret-references/evidgate-secret-references.html` |
|  | `integrations` | Integrations & webhooks | Tích hợp và webhook | 35 integrations-webhooks | `../35-integrations-webhooks/evidgate-webhook-integrations.html` |
|  | `agents` | Agent pools | Nhóm agent | 34 agent-pools | `../34-agent-pools/evidgate-agents-pools.html` |
|  | `quota` | Quota & retention | Hạn mức và lưu giữ | 31 quota-retention | `../31-quota-retention/evidgate-usage-retention.html` |
|  | `audit` | Audit log | Nhật ký kiểm toán | 32 audit-log | `../32-audit-log/evidgate-audit-log.html` |
|  | `security` | Security events | Sự kiện bảo mật | 33 security-events | `../33-security-events/evidgate-security-events.html` |
|  | `support` | Support access | Quyền truy cập hỗ trợ | 36 support-access | `../36-support-access/evidgate-support-access.html` |

Brand mark / Evidgate home -> 01 app-shell (`../01-app-shell/evidgate-workspace-shell.html`); it is the only sidebar link that is not one of the 25 items.

### Detail pages (no own sidebar item; the parent item is highlighted)

| page | `data-nav` (highlighted item) | rule |
|---|---|---|
| 01 app-shell | `runs` | app shell shows run-1842 with Runs selected (01 brief) |
| 04 run-detail | `runs` | run detail |
| 05 evidence-panel | `runs` | evidence panel is a tab of run detail (same file as 04) |
| 06 decision-waivers | `runs` | stored decision and waivers of a run |
| 07 report-comparison | `runs` | producer report vs normalized evidence of a run |
| 09 failure-detail-triage | `failures` | incident detail opened from the Failure Inbox |
| 10 investigation-history | `failures` | case history opened from an incident |
| 11 artifact-viewer | `runs` | artifact viewer is reached from run evidence (coordinator ruling S49) |
| 13 healing-shadow-detail | `healing` | candidate detail opened from Healing Review |
| 14 healing-approval | `healing` | candidate approval (same file as 13) |
| 18 studio-draft-editor | `studio` | draft editor (same file as 17) |
| 19 studio-review-merge | `studio` | draft review and merge |

All other pages highlight their own item (`data-nav` equals the item id above). `aria-current="page"` is set when the page is the item's own target; for detail pages the parent item gets the `active` class and `aria-current="true"`.

Settings tabs inside pages 27-38 are page content, not part of the sidebar.


Page 39 (locale layout review) is design/development only and not in customer navigation; it is reachable from `index.html` only.

Shared files: 04/05 share `evidgate-run-detail.html`, 13/14 share `evidgate-candidate-detail.html`, 17/18 share `evidgate-test-studio.html`; links to these go to the first page's folder (04, 13, 17).
