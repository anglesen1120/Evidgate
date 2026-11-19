// Proofs for the per-value vocabulary exemptions of check-no-fixture-code.mjs: the error-detail
// `reason` words and the export `format` discriminator are exempt only at their own position, and
// any other word there, or the same word elsewhere, is still reported as a fixture leak.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { ERROR_DETAIL_REASONS } from './check-no-fixture-code.vocabulary.mjs';

const SCRIPT = fileURLToPath(new URL('./check-no-fixture-code.mjs', import.meta.url));
const CONTRACT = fileURLToPath(new URL('../../../contracts/openapi.yaml', import.meta.url));

/** Runs the real gate over a synthetic corpus holding `document` and a bundle holding `word`. */
async function gate(document, word) {
  const root = await mkdtemp(join(tmpdir(), 'evidgate-vocab-'));
  try {
    const dist = join(root, 'dist');
    const corpus = join(root, 'corpus', 'probe');
    await mkdir(dist, { recursive: true });
    await mkdir(corpus, { recursive: true });
    await writeFile(join(dist, 'main.js'), `export const x = ${JSON.stringify(word)};\n`);
    await writeFile(join(corpus, 'input.json'), JSON.stringify(document));
    return spawnSync(process.execPath, [SCRIPT], {
      encoding: 'utf8',
      env: {
        ...process.env,
        EVIDGATE_WEB_DIST_DIR: dist,
        EVIDGATE_WEB_FIXTURE_DIR: join(root, 'corpus'),
        EVIDGATE_WEB_CONTRACT_FILE: CONTRACT,
      },
    });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

const detail = (reason) => ({ body: { error: { details: [{ path: '/steps', reason }] } } });

test('the vocabulary holds exactly the eight documented reason words', () => {
  assert.deepEqual([...ERROR_DETAIL_REASONS].sort(), [
    'body_too_large',
    'code_conflict',
    'duplicate_key',
    'invalid_text',
    'steps_invalid',
    'unknown_parent',
    'unsupported_format',
    'value_invalid',
  ]);
});

for (const word of ERROR_DETAIL_REASONS) {
  test(`reason ${word} is exempt under body.error.details[]`, async () => {
    const result = await gate(detail(word), word);
    assert.equal(result.status, 0, result.stderr);
  });
}

test('another reason word under body.error.details[] is still a leak', async () => {
  const word = 'some_other_reason';
  const result = await gate(detail(word), word);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /some_other_reason/);
});

test('an exempt reason word under a reason member elsewhere is still a leak', async () => {
  const result = await gate({ body: { note: { reason: 'duplicate_key' } } }, 'duplicate_key');
  assert.equal(result.status, 1);
  assert.match(result.stderr, /duplicate_key/);
});

const widened = {
  'a nested member': { body: { error: { details: [{ extra: { reason: 'duplicate_key' } }] } } },
  'an array value': { body: { error: { details: [{ reason: ['duplicate_key'] }] } } },
  'a nested array': { body: { error: { details: [[{ reason: 'duplicate_key' }]] } } },
  'a case variant': detail('Duplicate_Key'),
};
for (const [name, document] of Object.entries(widened)) {
  test(`reason under ${name} of body.error.details[] is still a leak`, async () => {
    const word = name === 'a case variant' ? 'Duplicate_Key' : 'duplicate_key';
    const result = await gate(document, word);
    assert.equal(result.status, 1);
  });
}

test('the export format discriminator is exempt, another format value is a leak', async () => {
  const exempt = await gate({ body: { format: 'authoring-import' } }, 'authoring-import');
  assert.equal(exempt.status, 0, exempt.stderr);
  const other = await gate({ body: { format: 'fixture-only-format' } }, 'fixture-only-format');
  assert.equal(other.status, 1);
  assert.match(other.stderr, /fixture-only-format/);
});

for (const kind of ['feature_node', 'checklist_item', 'manual_test_case']) {
  test(`the entity_kind value ${kind} is exempt`, async () => {
    const result = await gate({ body: { entity_kind: kind } }, kind);
    assert.equal(result.status, 0, result.stderr);
  });
}

test('another entity_kind word is still a leak', async () => {
  const other = await gate({ body: { entity_kind: 'fixture_only_kind' } }, 'fixture_only_kind');
  assert.equal(other.status, 1);
  assert.match(other.stderr, /fixture_only_kind/);
});

test('an entity_kind word under another member is still a leak', async () => {
  const other = await gate({ body: { note: 'feature_node' } }, 'feature_node');
  assert.equal(other.status, 1);
  assert.match(other.stderr, /feature_node/);
});
