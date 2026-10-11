# fixtures/threat-model/

Threat-model fixtures for story 11.1.1 (AC1–AC3, AC7), checked by `scripts/check-threat-model.sh --self-test`. Every file is synthetic (handcrafted for 11.1.1-T1.3): no customer data, no real control claims — the task ids, paths and RR ids exist only to exercise the lint.

This is _not_ a `<domain>/<name>/` fixture corpus: no directory here carries a `meta.json` or an `input.*` file, so the .NET and TypeScript fixture loaders skip it.

## Layout

```
fixtures/threat-model/
  ok/         # models the lint must accept (exit 0)
  negative/   # models the lint must reject (exit 1), each with a sibling *.expect
```

`ok/minimal.md` is a small but fully valid model: all 22 catalogue ids (11 modules, 11 boundaries) with one threat row each, a register entry per state, the ritual slot and log tables, the §7 summary table, and a citation of every 00 §5.7 security rule S1..S8.

## `*.expect` convention

Every `negative/<name>.md` has a sibling `negative/<name>.expect` holding the exact substring the lint must print (its `E <file>: <problem>` line). `scripts/check-threat-model.sh --self-test` runs the real model first, then every fixture, and prints one line per failure:

- the real model must exit 0;
- each `ok/` fixture must exit 0;
- each `negative/` fixture must exit 1 and print its `.expect` substring (a non-empty `.expect` is required, and every `.md` needs its `.expect` and vice versa);
- the run fails when there are no negative or no ok fixtures.

## Negative fixtures

One fixture per lint rule (54 negative fixtures in all):

- `missing-module-section` — a required module's catalogue row and section are both gone;
- `catalogue-row-without-section` — a catalogue id with no `### <id> — <name>` heading;
- `section-without-catalogue-row` — a `### <id> — <name>` heading with no catalogue row;
- `section-without-rows` — a section heading with no threat table;
- `undispositioned-row` — a threat row whose Disposition is neither `control:` nor `accepted:`;
- `open-row` — a threat row left `open:`;
- `disposition-two-markers` — a Disposition carrying both a `control:` and an `accepted:` marker;
- `unknown-task-id` — a control citing `99.9.9-T9`, which no area plan defines;
- `control-no-task-id` — a `control:` citing no task id at all;
- `control-without-tag` — a `control:` with no `[implemented: …]` / `[planned]` tag;
- `control-two-tags` — a `control:` carrying both an `[implemented: …]` and a `[planned]` tag;
- `implemented-path-missing` — an `[implemented: …]` path that does not exist in the repo;
- `implemented-path-absolute` — an `[implemented: …]` path that is absolute (`/etc/hosts`);
- `implemented-path-backslash` — an `[implemented: …]` path containing a backslash;
- `implemented-path-directory` — an `[implemented: …]` path that is a directory (`scripts`);
- `implemented-path-escape` — an `[implemented: …]` path with a `..` segment;
- `implemented-path-untracked` — an `[implemented: …]` path that exists but is gitignored (`node_modules/.package-lock.json`);
- `implemented-tag-unclosed` — an `[implemented: …` tag with no closing bracket;
- `implemented-tag-empty` — an `[implemented: ]` tag with no path;
- `accepted-missing-rr` — an `accepted:` citing an RR with no register entry;
- `accepted-open-decision-rr` — an `accepted:` citing an `open-decision` register entry;
- `accepted-no-rr` — an `accepted:` that cites no RR at all;
- `accepted-rr-text-only` — an `accepted:` whose RR id is not the first token after the colon;
- `register-accepted-no-owner` — an accepted register entry with an empty owner;
- `register-accepted-no-rationale` — an accepted register entry with an empty rationale;
- `register-bad-review-date` — a register entry whose `Reviewed` is not an ISO date;
- `register-impossible-date` — a register entry whose `Reviewed` is a well-shaped but impossible date (`2026-13-45`);
- `register-bad-next-review` — a register entry whose `Next review` is neither `M2`/`M3` nor an ISO date;
- `register-next-before-reviewed` — a register entry whose `Next review` date is before `Reviewed`;
- `register-open-no-open-marker` — an `open-decision` rationale without `[OPEN]`;
- `register-open-bad-receiving` — an `open-decision` receiving task that no area plan defines;
- `duplicate-register-id` — two register rows sharing one `RR-<n>` id;
- `uncited-rr` — a register entry no threat row cites;
- `duplicate-threat-id` — the same `TH-<suffix>-<n>` id in two sections;
- `duplicate-section-id` — two `### M-identity` sections;
- `duplicate-catalogue-id` — two catalogue rows sharing one id;
- `bad-stride` — a STRIDE cell with a letter outside `S T R I D E`;
- `threat-row-seven-cells` — a threat row with 7 cells (exactly 6 are required);
- `stray-table-row` — a threat row after a blank line, outside the one parsed table;
- `non-table-line-in-table` — a non-pipe, non-blank line inside a threat table;
- `duplicate-threat-table` — a second threat table with the same header in one section;
- `html-comment` — an HTML comment (a commented-out section must not satisfy a check);
- `indented-code-block` — a 4-space indented code block;
- `fence-wrong-marker` — a backtick fence "closed" with a tilde run, so it stays unclosed;
- `fence-short-close` — a 4-backtick fence "closed" by a 3-backtick run, so it stays unclosed;
- `cell-too-large` — a table cell over the 8 KiB cap;
- (generated at run time, not stored: a model file over the 2 MiB cap);
- `rule-s6-uncited` — no threat row cites 00 §5.7 rule S6;
- `ritual-missing-m3` — the milestone slot table without its `M3` row;
- `ritual-bad-date` — a slot row whose `Last review` is neither an ISO date nor `pending`;
- `ritual-impossible-date` — a slot row whose `Last review` is a well-shaped but impossible date (`2026-02-30`);
- `log-missing` — the milestone log table is absent;
- `log-impossible-date` — a log row whose `Date` is a well-shaped but impossible date (`2026-02-30`);
- `unclosed-fence` — a code fence opened and never closed (it would swallow the rest of the model);
- `summary-mismatch` — a §7 summary count that disagrees with the computed counts.
