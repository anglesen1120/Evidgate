import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const source = readFileSync(new URL('../src/theme-init.js', import.meta.url), 'utf8');

function run(storage) {
  const attributes = new Map();
  const window = { localStorage: { getItem: storage } };
  const document = {
    documentElement: { setAttribute: (name, value) => attributes.set(name, value) },
  };
  runInNewContext(source, { window, document });
  return attributes;
}

test('applies a stored explicit choice before first paint', () => {
  for (const choice of ['light', 'dark']) {
    assert.equal(run(() => choice).get('data-theme'), choice);
  }
});

test('leaves data-theme unset when nothing valid is stored, so the CSS follows the OS', () => {
  assert.equal(run(() => null).has('data-theme'), false);
  assert.equal(run(() => 'sepia').has('data-theme'), false);
});

test('survives denied storage', () => {
  const denied = () => {
    throw new Error('denied');
  };
  assert.equal(run(denied).has('data-theme'), false);
});
