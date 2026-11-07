# `vi` review and concatenation audit for the six core flows (09.8.1-T4.1)

Scope: every `vi` target used by the core flows of FR-09-12 — sign-in, scope/project switch, run
list, run detail (header, manifest, evidence, decision, attempts, producers, release, timeline),
onboarding wizard, shell navigation and app chrome, the shared state patterns and the error
patterns fed by the 00 §9.2 envelope. Settings, quota, suites and the authoring screens are owned
by other lanes and are out of scope except where a template joins translated fragments.

Method: the `vi` catalog was parsed and every unit whose id belongs to `auth.*`, `shell.*`,
`runs.*`, `onboarding.*`, `shared.*` and `ui.close` (624 units) was compared against its English
source and against the glossary in [`plans/00-overview.md` §3](../../plans/00-overview.md). The
catalog invariants that a fix must not break were read from
[`apps/web/scripts/check-i18n-keys.mjs`](../../apps/web/scripts/check-i18n-keys.mjs): every id of
`messages.xlf` must exist in `messages.vi.xlf` with a non-empty `<target>` and an identical
placeholder-id set; an id that the source catalog does not declare is a stale-key error; ids must
match `@@area.name`. `apps/web/src/app/shared/ui/i18n-catalog.spec.ts` adds that no `shared.*`
target may equal its source.

Status: the fix set below is **applied** in this change. Every `<target>`-only replacement of §1,
§2 and §3 is in `apps/web/src/locale/messages.vi.xlf`, the one row that also changes the English
source (`runs.detail.attempts.page`) is in both `messages.vi.xlf` and `messages.xlf` with its
matching template edit, and every spec/e2e copy that asserted the old copy now asserts the new
copy. The changed ids are listed per group in §6; `shared.label-chip.healed`
stays as found, per §8. Nothing was re-extracted: the catalog was hand-edited, so `i18n:check` and
the spec runs named at the end of §6 are the checks to run on this state.

## 1. Terminology against the glossary

Canonical choice is named first; every row lists the ids that disagreed with it.

| Term | Canonical `vi` | Found elsewhere | Ids | Fix |
|---|---|---|---|---|
| run | `lượt chạy` (27 units) | `lần chạy` (6 units) | `runs.detail.release.select-label`, `runs.detail.release.unbound`, `runs.detail.release.bound-run`, `runs.detail.producers.empty-description`, `runs.detail.producers.run-after-cancel-one`, `runs.detail.producers.run-after-cancel` | replace `lần chạy` with `lượt chạy` |
| producer | `nguồn gửi dữ liệu` (5 units) | `bên gửi` (3 units), `producer` left in English (2 units), `bên tạo bằng chứng` (1 unit) | `runs.list.empty-description`, `runs.detail.producers`, `runs.detail.no-producers`, `runs.evidence.producer-link`, `runs.evidence.reason.stale-epoch`, `shared.gallery.reason-plain` | use `nguồn gửi dữ liệu` in all six |
| live vs official evaluation | `đánh giá hiện hành` for *live*; `đánh giá chính thức` only for *official* | *live* rendered `chính thức`, which collapsed two distinct concepts into one word | `runs.list.decision-hint`, `runs.evidence.no-evaluation`, `runs.evidence.evaluation-reference`, `runs.decision.title`, `runs.decision.loading`, `runs.decision.pending-description`, `runs.decision.unavailable`, `runs.decision.updated-announcement`, `runs.decision.shadow-description` | `chính thức` → `hiện hành` in these nine; `shared.label-chip.official` and `runs.detail.attempts.official` keep `chính thức` |
| shadow evaluation | `đánh giá đối chứng` | English word kept (2 units) | `shared.label-chip.shadow`, `runs.decision.shadow-ignored`, `runs.decision.shadow-description` | use `đánh giá đối chứng` |
| revision | `phiên bản` (43 units) | `bản sửa đổi`, `bản sửa` (6 units) | `runs.detail.environment-revision`, `shared.conflict.current-revision`, `shared.conflict.revision-unavailable`, `onboarding.enable.revision`, `onboarding.enable.hash`, `onboarding.enable.previous-revision` | use `phiên bản` |
| manifest | `bản kê` (4 units) | `bản kê khai` (2 units) | `runs.decision.manifest-hash`, `runs.evidence.reason.build-mismatch` | drop the second word |
| artifact | `tệp bằng chứng` | `hiện vật` (museum-exhibit register) | `runs.detail.attempts.artifacts`, `shared.gallery.reason-linked` | use `tệp bằng chứng` |
| shard | `phân đoạn` | `phân mảnh` | `runs.evidence.reason.missing-shard` | use `phân đoạn` |
| ingest token | `mã nạp dữ liệu` (8 units) | `Token thu thập dữ liệu`, bare `mã` | `onboarding.evidence.tokens`, `onboarding.connect.token-name`, `onboarding.connect.token-name-error`, `onboarding.connect.rotate`, `onboarding.connect.rotate-named`, `onboarding.connect.rotated` | `mã nạp dữ liệu` everywhere; keep `mã truy cập API` for `shell.nav.tokens` (different object) |
| epoch | `thế hệ` (`thế hệ nguồn gửi` for a producer epoch) | English `epoch` kept (4 units) | `runs.detail.producers.event-note`, `runs.detail.producers.stale-epoch`, `runs.detail.producers.stale-epoch-one`, `runs.evidence.reason.stale-epoch` | use `thế hệ cũ` / `thế hệ nguồn gửi` |

## 2. Wrong meaning, wrong reference, wrong register

| Id | English | `vi` found | Issue | Fix |
|---|---|---|---|---|
| `shell.nav.availability` | Availability | Trạng thái sửa lỗi | says "repair status"; the entry is the healing availability page | Tính sẵn sàng |
| `shell.nav.knowledge` | Knowledge lineage | Tri thức và thu hồi | second noun ("revocation") is not in the source | Nguồn gốc tri thức |
| `shell.nav.suites` | Suite composition | Bộ kiểm thử | drops *composition*; the page composes a release from suites | Thành phần bộ kiểm thử (same wording as `suites.composition.suite-composition`) |
| `shell.stub.body` | route, permission guard and navigation entry | Tuyến đường, bộ chặn quyền… | `tuyến đường` is a road, not a route | Đường dẫn, bộ chặn quyền… |
| `runs.evidence.selected-unavailable` | Selected revision unavailable | Không có phiên bản đã chọn | says "no revision is selected", not "the selected revision is unavailable" | Phiên bản đã chọn không khả dụng |
| `runs.evidence.empty-description` | No evidence verdict is inferred. | Không suy diễn kết luận từ bằng chứng. | turns a statement about the system into an imperative to the reader | Không có kết luận nào được suy ra từ bằng chứng. |
| `runs.evidence.invalid-body` | Evidence response unavailable: {code} | Không có phản hồi bằng chứng hợp lệ: {code} | adds "valid": the response was readable, its reason code was unknown | Phản hồi bằng chứng không khả dụng: {code} |
| `shared.gallery.request-host` | Route-level request host | Vùng trạng thái yêu cầu của trang | paraphrases a component role as a page region | Thành phần hiển thị trạng thái yêu cầu cấp trang |
| `shared.label-chip.healed` | Healed with candidate | Đã chữa lành với ứng viên | medical verb for an accepted script repair; in this domain "healing" is the product's own word for a script repair, so the medical reading is defensible (§8 judge dissent) | Đã tự sửa bằng ứng viên — judgement call, not a blocking defect |
| `runs.detail.timeline.not-reached` | Not reached | Không tới được | "cannot be reached"; the step simply was not reached | Không đạt tới |
| `shared.conflict.title` | Review conflict | Xem xét xung đột | verb phrase as a dialog title | Xung đột khi xem xét |
| `auth.login.destination-heading` | Your return destination | Đích trở về của bạn | `trở về` reads as a verb inside the noun phrase | Đích quay về của bạn |

## 3. Formality and punctuation consistency

- The copy addresses the reader with `bạn` and the imperative `Hãy` (19 units). Two shell strings
  are the only ones using the formal `Vui lòng`:
  `shell.page.unavailable.explanation` (`Không thể tải không gian làm việc. Vui lòng thử lại.`)
  and `shell.page.unavailable.failed` (`… vẫn chưa khả dụng. Vui lòng thử lại sau.`) → use
  `Hãy thử lại.` / `Hãy thử lại sau.`
- No target mixes `ông/bà`, `quý khách` or a second person form, and every target is NFC-normalised
  with no broken diacritics, no double spaces and no space before punctuation (checked over all 624
  core units).
- Terminal punctuation matches the source in all 624 units.

## 4. Identical-to-English targets

| Id | Target | Verdict |
|---|---|---|
| `shell.layout.brand` | Evidgate | ok, product name |
| `shell.nav.studio` | Test Studio | ok, product area name |
| `onboarding.connect.oidc-title` | GitHub Actions (OIDC) | ok, protocol/product name |
| `auth.login.locale-en`, `auth.login.locale-vi` | EN, VI | ok, language codes and `lang` attributes |

No defect in this class: every other core unit carries a Vietnamese target.

## 5. Concatenation audit

Every template and every `$localize` call under `apps/web/src/app` was searched for: a translated
fragment joined to a literal by `+` or a template literal, two `i18n` elements forming one
sentence, and plurals formed by appending `s`.

| Site | Pattern | Verdict |
|---|---|---|
| `features/runs/detail/attempts/attempts-table.component.html:193` | `{{ copy.page }} {{ page() + 1 }} / {{ pages().length }}` — translated noun, two numbers and the separator `/` are three fragments; `Page` cannot be moved by a translator | **bug** — make it one message: source `Page {page} / {pages}`, `vi` `Trang {page} / {pages}`, rendered by a computed label (same shape as `runs.detail.producers.page`, which already does this) |
| `features/settings/quota-retention/quota-panel.component.html:25` | `<span i18n="@@quota.panel.period">Billing period</span>:` — the colon sits outside the message | ok — the label is one complete noun and `: ` separates it from the value; folding the colon into the message deletes that separator, because the whitespace-only text node between the two spans is dropped, and renders `Billing period:2026-11` |
| `features/runs/detail/producers/producer-status-table.component.ts:98-114`, `features/runs/detail/timeline/run-timeline.component.ts:63-66` | manual singular/plural switch between two complete messages (`-one` and a `{count}` message) | ok — whole messages, no fragment; `vi` has no plural inflection, so both branches render correctly. No ICU message exists anywhere in this app, so this is the local convention, not a defect |
| `features/onboarding/health/cli-quickstart.component.ts:41-63` | `` `# ${$localize`:@@onboarding.quickstart.…`}` `` | ok — the `#` is shell-comment syntax and the `$localize` message is complete |
| `features/auth/login/login.page.ts:62-64` | `<span i18n>EN</span> / <span i18n>VI</span>` with `aria-hidden` separator | ok — two independent labels; the control's accessible name is the whole sentence `auth.login.switch-language` |
| `{{ copy.<label> }} {{ value }}` joins: `attempts-table.component.html:81`, `run-header.component.html:175`, `settings/members/role-scope-preview.component.ts:44`, `settings/policy/policy-summary.component.ts:101`, `settings/policy/policy.page.html:75`, `settings/policy/revision-diff.component.ts:47,70,153`, `settings/policy/revision-list.component.ts:40`, `suites/composition/suite-composition.component.html:225,276` | noun label followed by an identifier or number | ok — the label is a complete word and no punctuation is glued on; word order (noun + number) is identical in `en` and `vi`. `revision-diff.component.ts:153` is the only one built in TypeScript instead of the template |
| `shell/layout/shell.component.html`, `shared/ui/annotations/*`, `shared/ui/gallery/*` | `i18n` element followed by an interpolated value or a second `i18n` element | ok — label/value pairs and independent badges, never one sentence split across elements |
| `shared/ui/data-table/*` | captions and column headers arrive already localized from the caller | ok |

No template builds one sentence from two translated pieces, and no `$localize` message is built
from another translated string.

## 6. Fix set applied (mechanical)

Applied as target-only replacements in `apps/web/src/locale/messages.vi.xlf` — placeholders and
ids are unchanged, so `i18n:check` stays green: the nine *live* rows (§1), six `lần chạy` rows,
six producer rows, three shadow rows, six revision rows, two manifest rows, two artifact rows, one
shard row, six token rows, four epoch rows, the ten target-only rows of §2 plus `shell.stub.body`,
and the two `Vui lòng` → `Hãy` rows of §3 (`shared.label-chip.healed` is excluded from the fix set:
it is a judgement call, not a defect, per §8). Ids changed, by group:

- run → `lượt chạy`: `runs.detail.release.select-label`, `runs.detail.release.unbound`,
  `runs.detail.release.bound-run`, `runs.detail.producers.empty-description`,
  `runs.detail.producers.run-after-cancel-one`, `runs.detail.producers.run-after-cancel`.
- producer → `nguồn gửi dữ liệu`: `runs.list.empty-description`, `runs.detail.producers`,
  `runs.detail.no-producers`, `runs.evidence.producer-link`, `runs.evidence.reason.stale-epoch`,
  `shared.gallery.reason-plain`.
- live evaluation → `hiện hành`: `runs.list.decision-hint`, `runs.evidence.no-evaluation`,
  `runs.evidence.evaluation-reference`, `runs.decision.title`, `runs.decision.loading`,
  `runs.decision.pending-description`, `runs.decision.unavailable`,
  `runs.decision.updated-announcement`, `runs.decision.shadow-description`.
- shadow evaluation → `đánh giá đối chứng`: `shared.label-chip.shadow`,
  `runs.decision.shadow-ignored`, `runs.decision.shadow-description`.
- revision → `phiên bản`: `runs.detail.environment-revision`, `shared.conflict.current-revision`,
  `shared.conflict.revision-unavailable`, `onboarding.enable.revision`, `onboarding.enable.hash`,
  `onboarding.enable.previous-revision`.
- manifest → `bản kê`: `runs.decision.manifest-hash`, `runs.evidence.reason.build-mismatch`.
- artifact → `tệp bằng chứng`: `runs.detail.attempts.artifacts`, `shared.gallery.reason-linked`.
- shard → `phân đoạn`: `runs.evidence.reason.missing-shard`.
- ingest token → `mã nạp dữ liệu`: `onboarding.evidence.tokens`, `onboarding.connect.token-name`,
  `onboarding.connect.token-name-error`, `onboarding.connect.rotate`,
  `onboarding.connect.rotate-named`, `onboarding.connect.rotated`.
- epoch → `thế hệ cũ` / `thế hệ nguồn gửi`: `runs.detail.producers.event-note`,
  `runs.detail.producers.stale-epoch`, `runs.detail.producers.stale-epoch-one`,
  `runs.evidence.reason.stale-epoch`.
- §2 target-only rows: `shell.nav.availability`, `shell.nav.knowledge`, `shell.nav.suites`,
  `shell.stub.body`, `runs.evidence.selected-unavailable`, `runs.evidence.empty-description`,
  `runs.evidence.invalid-body`, `shared.gallery.request-host`,
  `runs.detail.timeline.not-reached`, `shared.conflict.title`,
  `auth.login.destination-heading`.
- §3 formality rows: `shell.page.unavailable.explanation`, `shell.page.unavailable.failed`.

One row also changes the English source, and carries the matching template edit and the same
source text in `apps/web/src/locale/messages.xlf`:

- `runs.detail.attempts.page`: source `Page` → `Page <x id="page"/> / <x id="pages"/>`, target
  `Trang` → `Trang <x id="page"/> / <x id="pages"/>`, and
  `attempts-table.component.html:193` renders one computed label (`pageLabel()`, same shape as
  `runs.detail.producers.page`) instead of `{{ copy.page }}`.
  The English output stays `Page 2 / 3`, so `attempts-table.spec.ts` assertions do not change.

Copies of the changed `vi` text, changed with the catalog (they assert shipped copy):

- `apps/web/e2e/shell/run-detail-release-timeline.spec.ts:20-21` (`lần chạy` → `lượt chạy`).
- `apps/web/e2e/shell/run-detail-evidence.spec.ts:44` (`Quyết định chính thức mới nhất` →
  `Quyết định hiện hành mới nhất`).
- `apps/web/src/app/features/runs/detail/producers/producer-status-table.component.spec.ts:264,275`
  (`…của lần chạy này` → `…của lượt chạy này`) and `:300,314` (`epoch cũ` → `thế hệ cũ`).

A later search for the replaced fragments (`lần chạy`, `bên gửi`, `bản kê khai`, `phân mảnh`,
`Đánh giá shadow`, `đánh giá chính thức`, `epoch cũ`, `Xem producer`, `Tuyến đường`,
`Token thu thập`, `Không tới được`, `Xem xét xung đột`, `Đích trở về`, `Vui lòng thử lại`, …)
over the app sources and specs (`apps/web/src` and `apps/web/e2e`, excluding the two locale
catalogs) returns no spec or component hit, so no assertion still pins the old copy; the only hit
is `apps/web/src/app/features/runs/DESIGN-evidence-panel.md`, the design record §7 keeps as found.
The catalogs are excluded from that evidence because a few fragments legitimately survive inside
the replacements or inside units §7 leaves alone: `Thay mã` and `Tên mã` inside the new
`Thay mã nạp dữ liệu` / `Tên mã nạp dữ liệu` (`messages.vi.xlf:2214,2310,2150`), `chính thức`
inside the kept `Đánh giá chính thức` chip (`:338`, `runs.detail.attempts.official` at `:2855`),
`Bản sửa đổi` in the eight `suites.composition.*` units (`:2865-2905`) and `hiện vật` in
`quota.metric.artifact-gb-month` (`:3148`), both named in §7 as another lane's screens.

Discovery ran with codebase-memory: `search_code` over the two fragment sets returned only
`DESIGN-evidence-panel.md`, and `check_index_coverage` reports this worktree's paths as
`not_tracked`/`partial`, so every literal check above was then repeated on the worktree files.

Verification of the applied fix set: `node apps/web/scripts/check-i18n-keys.mjs`,
`npm run i18n:check -w web`, the `shared/ui/i18n-catalog.spec.ts` Vitest file, and the two
Playwright run-detail specs in both locales.

## 7. Deliberately not changed

- `runs.detail.pr` → `Số yêu cầu hợp nhất`, `runs.detail.fork` → `Bản phân nhánh`: established
  GitHub terms, no competing wording in the core flows.
- `shell.nav.audit` → `Nhật ký kiểm toán`: the standard Vietnamese term for an audit log.
- `runs.evidence.target-unavailable` → `Không có thông tin về đích liên kết`: `đích` is the
  established word for a destination in this catalog and here names the evidence target the
  missing reason points at.
- `onboarding.shell.*` "câu chuyện" wording shared with `settings.placeholder.body` and
  `shell.overview.arriving`: the idiom is used on three screens, one of them outside this slice;
  changing two of the three would create a new inconsistency.
- `suites.composition.*` (`bản sửa đổi` for *revision*) and `quota.metric.artifact-gb-month`
  (`hiện vật`): same defects as §1 but on screens owned by the Settings and Suites lanes; they are
  named here so their owners can align on the canonical terms above.
- `apps/web/src/app/features/runs/DESIGN-evidence-panel.md` repeats several `vi` targets from §1
  and §2. It is a design record, not a mirror of the shipped catalog (it still lists ids such as
  `runs.evidence.no-producer`, `runs.evidence.current-context` and `runs.decision.invalid-body`
  that no source declares), so aligning it would be a documentation rewrite rather than an i18n
  fix.

## 8. Triage of the findings

The 24 finding groups above were judged in one batch (`judge_batch`, one `choice` question with the
labels valid / duplicate / false-positive / out-of-scope) rather than by hand:
`b.status()` = `{done: 24, total: 24, failed: 0, model: "typesafe/jev-1.14.0", cost: 0.00049, running: false}`.

22 groups came back `valid`, none `duplicate` or `out-of-scope`, and 2 came back `false-positive`:

- `identical-en` (§4) — the judge agrees that product nouns and language codes need no translation.
- `healed-chip` (§2) — conf 0.37, so the label was re-read: "healing" is the product's own term for
  a script repair, which makes `chữa lành` defensible. The row is downgraded to a judgement call and
  is not counted as a defect to fix.
- Low-confidence but recalculated as correct: `manifest-term` (0.53) and `epoch-term` (0.59) are the
  same class of drift as the high-confidence `token-term` (0.88) and `shard-term` (0.87), and the
  `concat-label-value` row (0.41) stays `ok` on the evidence of §5: every one of those ten joins
  places a complete noun before a number or identifier with no punctuation glued on, and the noun
  order is the same in both languages.
- On reinspection the §5 colon row is `ok`, not a defect to apply: the change would delete the
  separator space between the label and the value — the colon and the whitespace after it are one
  text node, so a message-wrapped colon renders `Billing period:2026-11` — and the original markup
  and English source stand. That is why §6 has one source-changing row, not two.

## 9. Tools used

- codebase-memory `list_projects`: confirmed this worktree has no index of its own.
- codebase-memory `search_graph` (`i18n locale messages xlf translation`, and a second query for a
  translation helper) and `get_file_outline` for `plans/00-overview.md` and
  `plans/features/09-web-ui/09.8.1-core-flows-are-usable-and-proven.md`. Discovery ran against the
  `Volumes-Project-Evidgate` index because the worktree is not indexed; every cited path was then
  read from the worktree itself, so the coverage caveat does not affect the quoted lines.
- Literal searches over the two XLIFF catalogs and the `apps/web/src/app` templates were run as
  data/XML extraction (catalog pairs, term variants, NFC/spacing checks) and as `grep` for literal
  template text, which the graph does not index as symbols.
