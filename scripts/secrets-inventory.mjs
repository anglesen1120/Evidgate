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
const DAY_MS = 86_400_000;

// A value, not a name: long unbroken base64/hex, PEM armour, userinfo in a URL, or an
// assignment of a non-trivial value to a credential-looking word.
const VALUE_PATTERNS = [
  [/[A-Za-z0-9+/=_-]{32,}/, 'long base64/hex-like run'],
  [/-----BEGIN [A-Z ]+-----/, 'PEM armour'],
  [/\/\/[^\s/@:]+:[^\s/@]+@/, 'URL with userinfo'],
  [/\b(password|pwd|secret|token|key)\s*[=:]\s*\S{8,}/i, 'credential assignment'],
];

function githubSlug(heading) {
  return heading
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9 _-]/g, '')
    .replace(/ /g, '-');
}

function headingSlugs(text) {
  const slugs = new Set();
  for (const line of text.split('\n')) {
    const match = /^#{1,6}\s+(.*)$/.exec(line);
    if (match) slugs.add(githubSlug(match[1]));
  }
  return slugs;
}

function* stringFields(entry) {
  for (const [field, value] of Object.entries(entry)) {
    // `name` and `runbook` are constrained by a schema pattern, not scanned.
    if (field === 'name' || field === 'runbook') continue;
    if (typeof value === 'string') yield [field, value];
  }
}

function compileSchema(schema) {
  const ajv = new Ajv2020({ strict: false, allErrors: true });
  addFormats(ajv);
  return ajv.compile(schema);
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
    if (entry.owner !== entry.breakGlassHolder && inventory.soleOperator === true) {
      errors.push(
        `E secrets-inventory: ${where} owner and breakGlassHolder differ but soleOperator is true`,
      );
    }
    for (const [field, value] of stringFields(entry)) {
      for (const [pattern, label] of VALUE_PATTERNS) {
        if (pattern.test(value))
          errors.push(
            `E secrets-inventory: ${where} ${field} looks like a secret value (${label})`,
          );
      }
    }
    const [file, anchor] = entry.runbook.split('#');
    const text = readText(file);
    if (text === undefined) {
      errors.push(`E secrets-inventory: ${where} runbook file ${file} does not exist`);
    } else if (anchor !== undefined && !headingSlugs(text).has(anchor)) {
      errors.push(`E secrets-inventory: ${where} runbook heading #${anchor} not found in ${file}`);
    }
    if (entry.lastRotated !== null) {
      const due = Date.parse(entry.lastRotated) + entry.rotationDays * DAY_MS;
      if (due < now.getTime()) {
        const row = matrixSecrets.get(entry.name);
        const deployed = row?.required?.staging === true || row?.required?.prod === true;
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
        c.inventory.secrets[0].cadenceBasis = 'password=hunter2hunter2';
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
      (row) => row.secret === true && row.required?.staging !== true && row.required?.prod !== true,
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
