// Secret inventory check (infra/secrets-inventory.json vs infra/config-matrix.json).
//
// The inventory names every secret with its owner, store, cadence and break-glass
// holder and never holds a value. This module is called by scripts/check-env-matrix.mjs,
// so it runs inside the check-env-matrix gate stage; it has no CLI of its own.
//
// Errors: a matrix `secret: true` key without an entry, an entry the matrix does not
// have (unless `external: true`), a schema violation, an owner/holder that is not `owner` or a person's name, a
// runbook link whose file or heading is missing, a value-looking string, and a
// rotation overdue for a key that staging or prod requires. Overdue in dev only is a
// warning.

import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const PLACEHOLDERS = new Set([
  'tbd',
  'todo',
  'unknown',
  'none',
  'n/a',
  'na',
  'placeholder',
  'changeme',
  'xxx',
  'tba',
]);
const PLACEHOLDER_WORDS =
  /\b(tbd|todo|unknown|unassigned|assigned|determined|placeholder|none|team)\b/i;
const DAY_MS = 86_400_000;

// A value, not a name: long unbroken base64/hex, PEM armour, userinfo in a URL, or an
// assignment of a non-trivial value to a credential-looking word.
const VALUE_PATTERNS = [
  [/[A-Za-z0-9+/=_-]{32,}/, 'long base64/hex-like run'],
  [/-----BEGIN [A-Z ]+-----/, 'PEM armour'],
  [/\/\/[^\s/@:]+:[^\s/@]+@/, 'URL with userinfo'],
  [/\b(password|pwd|secret|token|key)\s*[=:]\s*["']?\S{3,}/i, 'credential assignment'],
];

function githubSlug(heading) {
  return heading
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9 _-]/g, '')
    .replace(/ /g, '-');
}

// Same rules GitHub applies: headings inside fenced code do not count and a repeated
// heading gets a -1, -2 ... suffix.
function headingSlugs(text) {
  const slugs = new Set();
  const counts = new Map();
  let fence = null;
  for (const line of text.split('\n')) {
    const marker = /^\s*(```+|~~~+)/.exec(line);
    if (marker) {
      if (fence === null) fence = marker[1][0];
      else if (marker[1][0] === fence) fence = null;
      continue;
    }
    if (fence !== null) continue;
    const match = /^#{1,6}\s+(.*)$/.exec(line);
    if (!match) continue;
    const slug = githubSlug(match[1]);
    const seen = counts.get(slug) ?? 0;
    counts.set(slug, seen + 1);
    slugs.add(seen === 0 ? slug : `${slug}-${seen}`);
  }
  return slugs;
}

// Every free-text string of the inventory; fields a schema pattern or enum already
// constrains (`name`, `runbook`, `store`, `id`) are not scanned.
const CONSTRAINED = new Set(['name', 'runbook', 'store', 'id']);
function* allStrings(value, path) {
  if (typeof value === 'string') yield [path, value];
  else if (Array.isArray(value)) {
    for (const [i, item] of value.entries()) yield* allStrings(item, `${path}[${i}]`);
  } else if (value !== null && typeof value === 'object') {
    for (const [field, item] of Object.entries(value)) {
      if (!CONSTRAINED.has(field))
        yield* allStrings(item, path === '' ? field : `${path}.${field}`);
    }
  }
}

const validators = new WeakMap();
function compileSchema(schema) {
  if (validators.has(schema)) return validators.get(schema);
  const ajv = new Ajv2020({ strict: false, allErrors: true });
  addFormats(ajv);
  const validate = ajv.compile(schema);
  validators.set(schema, validate);
  return validate;
}

// Pure evaluation: readText(relativePath) returns file text or undefined.
export function evaluateInventory({ matrix, inventory, schema, readText, now }) {
  const errors = [];
  const warnings = [];
  const validate = compileSchema(schema);
  if (!validate(inventory)) {
    for (const issue of validate.errors) {
      errors.push(`E secrets-inventory: schema ${issue.instancePath || '/'} ${issue.message}`);
    }
    return { errors, warnings, secretCount: 0 };
  }

  const matrixSecrets = new Map(
    matrix.keys.filter((row) => row.secret === true).map((row) => [row.key, row]),
  );
  for (const [path, value] of allStrings(inventory, '')) {
    for (const [pattern, label] of VALUE_PATTERNS) {
      if (pattern.test(value)) {
        errors.push(`E secrets-inventory: ${path} looks like a secret value (${label})`);
      }
    }
  }
  const seen = new Set();
  for (const entry of inventory.secrets) {
    const where = `infra/secrets-inventory.json ${entry.name}`;
    if (seen.has(entry.name)) errors.push(`E secrets-inventory: ${where} is listed twice`);
    seen.add(entry.name);
    if (!matrixSecrets.has(entry.name) && entry.external !== true) {
      errors.push(
        `E secrets-inventory: ${where} is not a secret: true key of infra/config-matrix.json (set external: true for a repo-level secret)`,
      );
    }
    for (const field of ['owner', 'breakGlassHolder']) {
      if (PLACEHOLDER_WORDS.test(entry[field])) {
        errors.push(`E secrets-inventory: ${where} ${field} is a placeholder '${entry[field]}'`);
      }
    }
    if (entry.owner !== entry.breakGlassHolder && inventory.soleOperator === true) {
      errors.push(
        `E secrets-inventory: ${where} owner and breakGlassHolder differ but soleOperator is true`,
      );
    }
    const [file, anchor] = entry.runbook.split('#');
    const text = readText(file);
    if (text === undefined) {
      errors.push(`E secrets-inventory: ${where} runbook file ${file} does not exist`);
    } else if (anchor !== undefined && !headingSlugs(text).has(anchor)) {
      errors.push(`E secrets-inventory: ${where} runbook heading #${anchor} not found in ${file}`);
    }
    if (entry.lastRotated !== null) {
      if (Date.parse(entry.lastRotated) > now.getTime()) {
        errors.push(
          `E secrets-inventory: ${where} lastRotated ${entry.lastRotated} is in the future`,
        );
      }
      const due = Date.parse(entry.lastRotated) + entry.rotationDays * DAY_MS;
      if (due < now.getTime()) {
        const row = matrixSecrets.get(entry.name);
        // Deployed = required at boot, or sourced from anything but the dev default.
        const deployed = ['staging', 'prod'].some(
          (env) => row?.required?.[env] === true || (row?.source?.[env] ?? 'default') !== 'default',
        );
        const message = `${where} rotation overdue since ${new Date(due).toISOString().slice(0, 10)}`;
        (deployed ? errors : warnings).push(
          `${deployed ? 'E' : 'W'} secrets-inventory: ${message}`,
        );
      }
    }
  }
  for (const key of matrixSecrets.keys()) {
    if (!seen.has(key)) {
      errors.push(
        `E secrets-inventory: ${key} is secret: true in infra/config-matrix.json but missing from infra/secrets-inventory.json`,
      );
    }
  }
  for (const item of inventory.open) {
    if (PLACEHOLDERS.has(item.receiver.toLowerCase())) {
      errors.push(`E secrets-inventory: open item ${item.id} has a placeholder receiver`);
    }
  }
  return { errors, warnings, secretCount: inventory.secrets.length };
}

function loadFrom(root) {
  const json = (rel) => JSON.parse(readFileSync(join(root, rel), 'utf8'));
  return {
    matrix: json('infra/config-matrix.json'),
    inventory: json('infra/secrets-inventory.json'),
    schema: json('infra/secrets-inventory.schema.json'),
    readText: (rel) => {
      const path = join(root, rel);
      return existsSync(path) ? readFileSync(path, 'utf8') : undefined;
    },
  };
}

export function checkSecretsInventory(root, now = new Date()) {
  return evaluateInventory({ ...loadFrom(root), now });
}

// Each case mutates a deep copy of the real files and must fail with the marker;
// the unmutated pair must pass, so the cases also prove the committed files.
export function selfTestSecretsInventory(root, now = new Date()) {
  const base = loadFrom(root);
  const clone = (value) => JSON.parse(JSON.stringify(value));
  const firstSecret = base.inventory.secrets[0].name;
  const cases = [
    ['inventory-clean', () => {}, null],
    [
      'secret-missing-from-inventory',
      (c) => {
        c.inventory.secrets.shift();
      },
      `${firstSecret} is secret: true in infra/config-matrix.json but missing`,
    ],
    [
      'inventory-names-unknown-key',
      (c) => {
        c.inventory.secrets.push({ ...c.inventory.secrets[0], name: 'EVIDGATE_NOT_IN_MATRIX' });
      },
      'EVIDGATE_NOT_IN_MATRIX is not a secret: true key',
    ],
    [
      'external-entry-allowed',
      (c) => {
        c.inventory.secrets.push({
          ...c.inventory.secrets[0],
          name: 'EVIDGATE_REPO_CI_SECRET',
          external: true,
        });
      },
      null,
    ],
    [
      'duplicate-entry',
      (c) => {
        c.inventory.secrets.push(clone(c.inventory.secrets[0]));
      },
      'is listed twice',
    ],
    [
      'missing-owner',
      (c) => {
        delete c.inventory.secrets[0].owner;
      },
      "must have required property 'owner'",
    ],
    [
      'missing-store',
      (c) => {
        delete c.inventory.secrets[0].store;
      },
      "must have required property 'store'",
    ],
    [
      'missing-cadence',
      (c) => {
        delete c.inventory.secrets[0].rotationDays;
      },
      "must have required property 'rotationDays'",
    ],
    [
      'zero-cadence',
      (c) => {
        c.inventory.secrets[0].rotationDays = 0;
      },
      'rotationDays',
    ],
    [
      'null-rotation-without-reason',
      (c) => {
        delete c.inventory.secrets[0].lastRotatedReason;
      },
      "must have required property 'lastRotatedReason'",
    ],
    [
      'placeholder-owner',
      (c) => {
        c.inventory.secrets[0].owner = 'TBD';
      },
      '/secrets/0/owner must match pattern',
    ],
    [
      'empty-holder',
      (c) => {
        c.inventory.secrets[0].breakGlassHolder = '';
      },
      'breakGlassHolder',
    ],
    [
      'holder-differs-under-sole-operator',
      (c) => {
        c.inventory.secrets[0].breakGlassHolder = 'Ada Lovelace';
      },
      'owner and breakGlassHolder differ',
    ],
    [
      'value-in-free-text',
      (c) => {
        c.inventory.secrets[0].lastRotatedReason =
          'rotated to wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY99';
      },
      'looks like a secret value (long base64/hex-like run)',
    ],
    [
      'credential-assignment',
      (c) => {
        c.inventory.secrets[0].cadenceBasis = `${'pass'}word=hunter2hunter2`;
      },
      'looks like a secret value (credential assignment)',
    ],
    [
      'value-in-extra-field',
      (c) => {
        c.inventory.secrets[0].value = 'x';
      },
      'must NOT have additional properties',
    ],
    [
      'runbook-file-missing',
      (c) => {
        c.inventory.secrets[0].runbook = 'docs/runbooks/no-such-runbook.md';
      },
      'runbook file docs/runbooks/no-such-runbook.md does not exist',
    ],
    [
      'runbook-heading-missing',
      (c) => {
        c.inventory.secrets[0].runbook = 'docs/runbooks/key-rotation.md#99-no-such-heading';
      },
      'runbook heading #99-no-such-heading not found',
    ],
    [
      'overdue-for-deployed-key-is-error',
      (c) => {
        Object.assign(c.inventory.secrets[0], { lastRotated: '2000-01-01' });
        delete c.inventory.secrets[0].lastRotatedReason;
      },
      'rotation overdue since',
    ],
    [
      'whitespace-rotation-reason',
      (c) => {
        c.inventory.secrets[0].lastRotatedReason = '          ';
      },
      'lastRotatedReason',
    ],
    [
      'future-rotation-date',
      (c) => {
        Object.assign(c.inventory.secrets[0], { lastRotated: '2999-01-01' });
        delete c.inventory.secrets[0].lastRotatedReason;
      },
      'is in the future',
    ],
    [
      'multi-word-placeholder-owner',
      (c) => {
        c.inventory.secrets[0].owner = 'Not Assigned';
        c.inventory.secrets[0].breakGlassHolder = 'Not Assigned';
      },
      "owner is a placeholder 'Not Assigned'",
    ],
    [
      'value-in-open-text',
      (c) => {
        c.inventory.open[0].text = 'recovery wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY99 note';
      },
      'open[0].text looks like a secret value',
    ],
    [
      'value-in-sole-operator-note',
      (c) => {
        c.inventory.soleOperatorNote = 'wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY99';
      },
      'soleOperatorNote looks like a secret value',
    ],
    [
      'short-credential-assignment',
      (c) => {
        c.inventory.secrets[0].cadenceBasis = `${'pass'}word="abc"`;
      },
      'credential assignment',
    ],
    [
      'runbook-anchor-only-in-code-fence',
      (c) => {
        c.inventory.secrets[0].runbook =
          'docs/runbooks/environments.md#per-environment-smoke-against-a-running-environment';
      },
      'runbook heading #per-environment-smoke',
    ],
    [
      'overdue-optional-deployed-key-is-error',
      (c) => {
        const e = c.inventory.secrets.find(
          (x) => x.name === 'EVIDGATE__OIDC__GOOGLE__CLIENTSECRET',
        );
        Object.assign(e, { lastRotated: '2000-01-01' });
        delete e.lastRotatedReason;
      },
      'EVIDGATE__OIDC__GOOGLE__CLIENTSECRET rotation overdue',
    ],
    [
      'open-item-placeholder-receiver',
      (c) => {
        c.inventory.open[0].receiver = 'TBD';
      },
      'placeholder receiver',
    ],
  ];
  const results = [];
  for (const [name, mutate, marker] of cases) {
    const copy = { ...base, matrix: clone(base.matrix), inventory: clone(base.inventory) };
    mutate(copy);
    const { errors } = evaluateInventory({ ...copy, now });
    const ok = marker === null ? errors.length === 0 : errors.some((line) => line.includes(marker));
    results.push({
      name,
      ok,
      detail: ok
        ? ''
        : ` expected ${marker === null ? 'no errors' : JSON.stringify(marker)}, got ${errors.slice(0, 3).join(' | ') || 'none'}`,
    });
  }
  // Overdue only in dev: a key no deployed environment requires is a warning.
  const devOnly = clone(
    base.matrix.keys.find(
      (row) =>
        row.secret === true &&
        ['staging', 'prod'].every(
          (env) => row.required?.[env] !== true && row.source?.[env] === 'default',
        ),
    ),
  );
  const devCopy = { ...base, matrix: clone(base.matrix), inventory: clone(base.inventory) };
  const devEntry = devCopy.inventory.secrets.find((entry) => entry.name === devOnly.key);
  Object.assign(devEntry, { lastRotated: '2000-01-01' });
  delete devEntry.lastRotatedReason;
  const dev = evaluateInventory({ ...devCopy, now });
  const devOk =
    dev.errors.length === 0 && dev.warnings.some((line) => line.includes('rotation overdue'));
  results.push({
    name: 'overdue-for-dev-only-key-is-warning',
    ok: devOk,
    detail: devOk ? '' : ` errors=${dev.errors.join(' | ')} warnings=${dev.warnings.join(' | ')}`,
  });
  return results;
}
