// Proofs for `check-a11y-exceptions.mjs`: an accepted violation must carry all five fields, must
// name a rule axe-core knows, and must expire, so the list cannot turn into a permanent rule switch
// off. `validate` takes the day it judges against, so these cases never depend on the clock; the
// two CLI cases run the real script over a temporary file to prove the exit code `npm test` reads.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { validate } from './check-a11y-exceptions.mjs';

const SCRIPT = fileURLToPath(new URL('./check-a11y-exceptions.mjs', import.meta.url));
const TODAY = '2026-11-07';

/** One entry that passes every rule, so a case can break exactly what it names. */
const GOOD_ENTRY = {
  rule: 'color-contrast',
  selector: '#main-content > p.notice',
  reason:
    'Brand notice text is being moved onto the raised surface; token fix tracked in the plan.',
  owner: 'web-ui',
  'review-by': '2027-01-31',
};

function documentWith(...entries) {
  return { $schema: './a11y-exceptions.schema.json', exceptions: entries };
}

/** The problems of one document holding a single entry. */
function problemsOf(entry) {
  return validate(documentWith(entry), TODAY);
}

/** Asserts a case produced exactly the one problem it is about, and quotes every problem if not. */
function expectProblem(entry, pattern) {
  const problems = problemsOf(entry);
  assert.equal(problems.length, 1, problems.join('\n'));
  assert.match(problems[0], pattern);
}

test('an empty list passes: the product claims zero violations until one is accepted', () => {
  assert.deepEqual(validate(documentWith(), TODAY), []);
});

test('a complete entry inside the review window passes', () => {
  assert.deepEqual(validate(documentWith(GOOD_ENTRY), TODAY), []);
});

test('a review-by dated exactly today passes; yesterday has expired', () => {
  assert.deepEqual(validate(documentWith({ ...GOOD_ENTRY, 'review-by': TODAY }), TODAY), []);
  expectProblem({ ...GOOD_ENTRY, 'review-by': '2026-11-06' }, /review-by 2026-11-06 has passed/);
});

test('a review-by later than 90 days out fails', () => {
  assert.deepEqual(validate(documentWith({ ...GOOD_ENTRY, 'review-by': '2027-02-05' }), TODAY), []);
  expectProblem({ ...GOOD_ENTRY, 'review-by': '2027-02-06' }, /later than 90 days out/);
});

test('a missing owner fails and the other fields stay silent', () => {
  const { owner, ...withoutOwner } = GOOD_ENTRY;
  assert.equal(owner, 'web-ui');
  expectProblem(withoutOwner, /exceptions\[0\]\.owner must be a non-empty string/);
});

test('an empty field fails like a missing one', () => {
  expectProblem({ ...GOOD_ENTRY, reason: '   ' }, /exceptions\[0\]\.reason must be a non-empty/);
});

test('a rule id axe-core does not know fails, so a typo cannot hide a violation', () => {
  expectProblem({ ...GOOD_ENTRY, rule: 'color-contrat' }, /is not an axe-core rule id/);
});

test('a date that is misspelt or names no day fails', () => {
  expectProblem({ ...GOOD_ENTRY, 'review-by': '31-01-2027' }, /is not a YYYY-MM-DD day/);
  expectProblem({ ...GOOD_ENTRY, 'review-by': '2027-02-30' }, /is not a YYYY-MM-DD day/);
});

test('an unknown key on an entry fails, so a field nobody reads cannot sit there', () => {
  expectProblem({ ...GOOD_ENTRY, severity: 'minor' }, /has unknown key "severity"/);
});

test('a duplicate entry fails: one selector cannot be excused twice', () => {
  const problems = validate(documentWith(GOOD_ENTRY, GOOD_ENTRY), TODAY);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /exceptions\[1\] repeats color-contrast at #main-content/);
});

test('an unknown top-level key fails and a missing list fails', () => {
  const unknownKey = validate({ exceptions: [], schema: 1 }, TODAY);
  assert.deepEqual(unknownKey, ['unknown top-level key "schema"']);
  assert.deepEqual(validate({ $schema: 'x' }, TODAY), ['"exceptions" must be an array']);
});

test('the day it judges against must itself be a day', () => {
  const problems = validate(documentWith(), '2026-13-01');
  assert.deepEqual(problems, ['today "2026-13-01" is not a YYYY-MM-DD day']);
});

test('the script exits 0 on a valid list and 1 on one missing a field', async (t) => {
  const root = await mkdtemp(join(tmpdir(), 'evidgate-a11y-exceptions-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const good = join(root, 'good.json');
  const bad = join(root, 'bad.json');
  await writeFile(good, JSON.stringify(documentWith(GOOD_ENTRY)));
  const { owner, ...withoutOwner } = GOOD_ENTRY;
  assert.equal(owner, 'web-ui');
  await writeFile(bad, JSON.stringify(documentWith(withoutOwner)));

  const pass = spawnSync(process.execPath, [SCRIPT, `--today=${TODAY}`, `--file=${good}`], {
    encoding: 'utf8',
  });
  assert.equal(pass.status, 0, pass.stderr);
  assert.match(pass.stdout, /1 entries, 0 problems/);

  const fail = spawnSync(process.execPath, [SCRIPT, `--today=${TODAY}`, `--file=${bad}`], {
    encoding: 'utf8',
  });
  assert.equal(fail.status, 1);
  assert.match(fail.stderr, /exceptions\[0\]\.owner must be a non-empty string/);
});
