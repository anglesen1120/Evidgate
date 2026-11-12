#!/usr/bin/env node
// Per-environment secret placeholders, derived from infra/secrets-inventory.json (the
// single source: every entry carries a `generator` rule, there is no second list here).
//
//   dev                 fills .env.dev so ISecretProvider resolves every required secret on a
//                       fresh boot: copies .env.dev.template when the file is missing, resolves
//                       its __NAME__ tokens, then appends every key the rules can generate and
//                       the file still lacks. An existing value is never changed.
//   staging | prod      writes a git-ignored file of named, empty-valued entries (one per secret
//                       the matrix sources from the secret store) for the operator to fill in
//                       the store; an existing entry, empty or not, is kept as it is.
//
// Generator rules (inventory `generator.kind`):
//   random-hex          24 random bytes as hex; `role` links the password to its database role
//   random-base64       32 random bytes as base64 (peppers and signing keys need at least 32)
//   connection-string   Npgsql string for `role`, built from that role's random-hex password
//   template-provided   a committed local-only value the template must already define
//   not-generated       no value is ever created here (`reason` says why)
//
// A secret without a rule, or with an unknown one, fails before anything is written. No value
// is ever printed: output names keys only.
//
// usage: node scripts/env/secret-placeholders.mjs --env dev|staging|prod [--root <dir>]
//          [--file <path>] [--template <path>]

import { randomBytes } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const KINDS = new Set([
  'random-hex',
  'random-base64',
  'connection-string',
  'template-provided',
  'not-generated',
]);
const DEFAULT_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const TOKEN = /(?<![A-Za-z0-9_])__(EVIDGATE[A-Z0-9_]*?)__(?![A-Za-z0-9_])/g;

function fail(message) {
  process.stderr.write(`secret-placeholders: ${message}\n`);
  process.exit(2);
}

function parseArgs(argv) {
  const args = { root: DEFAULT_ROOT };
  for (let i = 0; i < argv.length; i += 2) {
    const flag = argv[i];
    const value = argv[i + 1];
    if (!['--env', '--root', '--file', '--template'].includes(flag) || value === undefined) {
      fail(
        `unknown or incomplete argument '${flag}' (usage: --env dev|staging|prod [--root dir] [--file path] [--template path])`,
      );
    }
    args[flag.slice(2)] = flag === '--env' ? value : resolve(value);
  }
  if (!['dev', 'staging', 'prod'].includes(args.env)) fail('--env must be dev, staging or prod');
  return args;
}

// Fails loudly, naming every offender, before any file is touched.
function loadRules(root) {
  const inventory = JSON.parse(readFileSync(join(root, 'infra', 'secrets-inventory.json'), 'utf8'));
  const problems = [];
  const rules = inventory.secrets.map((entry) => {
    const rule = entry.generator;
    if (!rule) problems.push(`${entry.name}: no generator rule in infra/secrets-inventory.json`);
    else if (!KINDS.has(rule.kind))
      problems.push(`${entry.name}: unknown generator kind '${rule.kind}'`);
    else if (rule.kind === 'not-generated' && !rule.reason)
      problems.push(`${entry.name}: not-generated needs a reason`);
    return { name: entry.name, ...rule };
  });
  for (const rule of rules) {
    if (rule.kind !== 'connection-string') continue;
    const owner = rules.find((r) => r.kind === 'random-hex' && r.role === rule.role);
    if (!rule.role || !owner)
      problems.push(`${rule.name}: no random-hex rule for role '${rule.role}'`);
  }
  if (problems.length > 0) fail(`inventory is not generatable:\n  ${problems.join('\n  ')}`);
  return rules;
}

const hex = () => randomBytes(24).toString('hex');
const base64 = () => randomBytes(32).toString('base64');

function lineFor(name) {
  return new RegExp(`^(?:export[ \\t]+)?${name}=(.*)$`, 'm');
}

function valueOf(content, name) {
  const match = lineFor(name).exec(content);
  if (!match) return undefined;
  return match[1].trim().replace(/^(['"])(.*)\1$/, '$2');
}

// Writes via a mode-600 temp file and rename, so a crash never leaves a half file.
function writePrivate(path, content) {
  mkdirSync(dirname(path), { recursive: true });
  const temp = `${path}.${process.pid}.tmp`;
  writeFileSync(temp, content, { mode: 0o600 });
  chmodSync(temp, 0o600);
  renameSync(temp, path);
}

function generateDev(root, rules, file, template) {
  const existed = existsSync(file);
  let content;
  if (existed) content = readFileSync(file, 'utf8');
  else if (existsSync(template)) content = readFileSync(template, 'utf8');
  else fail(`missing ${template}`);

  const written = [];
  const byName = new Map(rules.map((r) => [r.name, r]));

  // __NAME__ tokens: one fresh value per name, for random rules only.
  const tokens = new Set([...content.matchAll(TOKEN)].map((m) => m[1]));
  for (const name of tokens) {
    const rule = byName.get(name);
    if (!rule || !rule.kind.startsWith('random-')) {
      fail(`${name}: placeholder token in the template has no random generator rule`);
    }
    content = content.split(`__${name}__`).join(rule.kind === 'random-hex' ? hex() : base64());
    written.push(name);
  }

  const missing = (name) => {
    const value = valueOf(content, name);
    return value === undefined || value === '';
  };
  const append = (name, value) => {
    const line = `${name}=${value}`;
    content = lineFor(name).test(content)
      ? content.replace(lineFor(name), () => line)
      : `${content.replace(/\n*$/, '\n')}${line}\n`;
    written.push(name);
  };

  for (const rule of rules) {
    if (rule.kind === 'random-hex' && missing(rule.name)) append(rule.name, hex());
    else if (rule.kind === 'random-base64' && missing(rule.name)) append(rule.name, base64());
  }
  const port = valueOf(content, 'EVIDGATE_DB_PORT') || '5432';
  for (const rule of rules) {
    if (rule.kind !== 'connection-string' || !missing(rule.name)) continue;
    const owner = rules.find((r) => r.kind === 'random-hex' && r.role === rule.role);
    const pairs = [
      ['Host', '127.0.0.1'],
      ['Port', port],
      ['Database', 'evidgate'],
      ['Username', rule.role],
      ['Password', valueOf(content, owner.name)],
    ];
    append(rule.name, `'${pairs.map(([key, value]) => `${key}=${value}`).join(';')}'`);
  }
  const absent = rules
    .filter((r) => r.kind === 'template-provided' && missing(r.name))
    .map((r) => r.name);
  if (absent.length > 0) fail(`${file} lacks the committed local value for: ${absent.join(', ')}`);

  const leftover = [...content.matchAll(TOKEN)].map((m) => m[1]);
  if (leftover.length > 0)
    fail(`unresolved placeholder tokens: ${[...new Set(leftover)].join(', ')}`);

  const unique = [...new Set(written)];
  if (!existed || unique.length > 0) writePrivate(file, content);
  process.stdout.write(
    unique.length === 0
      ? `secret-placeholders: dev ${file}: nothing to add\n`
      : `secret-placeholders: dev ${existed ? 'updated' : 'created'} ${file} (mode 600), generated: ${unique.join(', ')}\n`,
  );
}

function generateRemote(root, rules, env, file) {
  const matrix = JSON.parse(readFileSync(join(root, 'infra', 'config-matrix.json'), 'utf8'));
  const rows = new Map();
  const walk = (node) => {
    if (Array.isArray(node)) node.forEach(walk);
    else if (node && typeof node === 'object') {
      if (typeof node.key === 'string' && node.secret) rows.set(node.key, node);
      else Object.values(node).forEach(walk);
    }
  };
  walk(matrix);

  const wanted = rules.filter((r) => {
    const row = rows.get(r.name);
    return !row || row.source?.[env] === 'secret-store';
  });
  const existing = existsSync(file) ? readFileSync(file, 'utf8') : '';
  let content =
    existing ||
    `# ${env} secret placeholders: fill each value in the secret store, never in git.\n`;
  const added = [];
  for (const rule of wanted) {
    if (valueOf(content, rule.name) !== undefined) continue;
    content = `${content.replace(/\n*$/, '\n')}${rule.name}=\n`;
    added.push(rule.name);
  }
  if (!existing || added.length > 0) writePrivate(file, content);
  process.stdout.write(
    added.length === 0
      ? `secret-placeholders: ${env} ${file}: nothing to add\n`
      : `secret-placeholders: ${env} wrote empty entries to ${file} for: ${added.join(', ')}\n`,
  );
}

const args = parseArgs(process.argv.slice(2));
const rules = loadRules(args.root);
if (args.env === 'dev') {
  generateDev(
    args.root,
    rules,
    args.file ?? join(args.root, '.env.dev'),
    args.template ?? join(args.root, '.env.dev.template'),
  );
} else {
  generateRemote(
    args.root,
    rules,
    args.env,
    args.file ?? join(args.root, '.secrets', `${args.env}.placeholders.env`),
  );
}
