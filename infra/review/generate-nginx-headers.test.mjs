// The nginx snippet is the deployed policy, so it is checked here rather than by
// reading it: these tests are the only execution of generate-nginx-headers.mjs in
// the gate-free `infra/` tree.
//
//   node --test infra/review/generate-nginx-headers.test.mjs

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const SCRIPT = fileURLToPath(new URL('./generate-nginx-headers.mjs', import.meta.url));

function run(args = [], env = {}) {
  const base = { ...process.env };
  delete base.EVIDGATE_REVIEW_PUBLIC_ORIGIN;
  return execFileSync(process.execPath, [SCRIPT, ...args], {
    env: { ...base, ...env },
    encoding: 'utf8',
  });
}

/** The script's stderr, for a run that must fail. */
function failure(args = [], env = {}) {
  try {
    run(args, env);
  } catch (error) {
    return String(error.stderr ?? '');
  }
  return assert.fail('expected a non-zero exit');
}

test('hands the nonce to nginx and keeps the policy from csp.mjs', () => {
  const snippet = run();

  assert.match(snippet, /^add_header Content-Security-Policy "default-src 'none';/m);
  assert.match(snippet, /style-src 'self' 'nonce-\$request_id'/);
  assert.match(snippet, /sub_filter '__CSP_NONCE__' \$request_id;/);
  assert.match(snippet, /sub_filter_once off;/);
  assert.match(snippet, /frame-ancestors 'none'/);
  assert.match(snippet, /object-src 'none'/);
  assert.match(snippet, /require-trusted-types-for 'script'/);
  // Delegating the nonce must not leave a real one behind: the header would
  // then authorise a value that never appears in the document.
  assert.doesNotMatch(snippet, /'nonce-[A-Za-z0-9+/]{22}=='/);
  const directives = snippet
    .split('\n')
    .filter((line) => !line.startsWith('#'))
    .join('\n');
  assert.equal(directives.split('$request_id').length - 1, 2, 'header plus sub_filter');
});

test('adds HSTS, nosniff and no-referrer on every response', () => {
  const snippet = run();
  assert.match(snippet, /add_header Strict-Transport-Security "max-age=31536000" always;/);
  assert.match(snippet, /add_header X-Content-Type-Options "nosniff" always;/);
  assert.match(snippet, /add_header Referrer-Policy "no-referrer" always;/);
});

test('connect-src is self alone when no public origin is configured', () => {
  const snippet = run();
  assert.match(snippet, /connect-src 'self';/);
  assert.doesNotMatch(snippet, /https:\/\//, 'no origin may appear without one being configured');
});

test('appends the public origin to connect-src', () => {
  const snippet = run([], { EVIDGATE_REVIEW_PUBLIC_ORIGIN: 'https://review.example.com' });
  assert.match(snippet, /connect-src 'self' https:\/\/review\.example\.com;/);
});

test('refuses a public origin the app contract cannot express', () => {
  const stderr = failure([], { EVIDGATE_REVIEW_PUBLIC_ORIGIN: 'http://review.example.com' });
  assert.match(stderr, /https/);
});

test('refuses a public origin with a path', () => {
  const stderr = failure([], { EVIDGATE_REVIEW_PUBLIC_ORIGIN: 'https://review.example.com/app' });
  assert.match(stderr, /origin/i);
});

test('writes the snippet to --out', () => {
  const dir = mkdtempSync(join(tmpdir(), 'review-headers-'));
  try {
    const file = join(dir, 'security-headers.conf');
    assert.equal(run(['--out', file]), '');
    assert.match(readFileSync(file, 'utf8'), /sub_filter '__CSP_NONCE__' \$request_id;/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('is byte-identical across runs, so the image layer is reproducible', () => {
  assert.equal(run(), run());
});

test('rejects an unknown argument instead of ignoring it', () => {
  assert.match(failure(['--origin', 'https://review.example.com']), /unexpected argument/);
});
