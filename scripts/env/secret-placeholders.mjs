#!/usr/bin/env node
// Per-environment secret placeholders, derived from infra/secrets-inventory.json (the
// single source: every entry carries a `generator` rule, there is no second list here).
//
//   dev                 fills .env.dev so ISecretProvider resolves every required secret on a
//                       fresh boot: copies .env.dev.template when the file is missing, resolves
//                       its __NAME__ tokens, then appends every key the rules can generate and
//                       the file still lacks. An existing value is never changed.
//   staging | prod      writes a git-ignored (.env.*) file of named, empty-valued entries (one per secret
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
import {
  chmodSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
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
  return new RegExp(`^[ \\t]*(?:export[ \\t]+)?${name}=(.*)$`, 'gm');
}

// The last assignment wins when the file is sourced, so it is the one read and replaced.
function lastMatch(content, name) {
  return [...content.matchAll(lineFor(name))].at(-1);
}

function valueOf(content, name) {
  const match = lastMatch(content, name);
  if (!match) return undefined;
  return match[1].trim().replace(/^(['"])(.*)\1$/, '$2');
}

// A value inside an Npgsql connection string, quoted when it holds a separator, a quote or
// whitespace; the whole string is then single-quoted for the shell.
function dsnValue(value) {
  return /[;"'\s]/.test(value) || value === '' ? `"${value.replaceAll('"', '""')}"` : value;
}

const shellSingleQuoted = (value) => `'${value.replaceAll("'", "'\\''")}'`;

// A plain token stays as the template wrote it; anything the shell would split or expand is quoted.
const shellValue = (value) =>
  /^[A-Za-z0-9_.,:/@%+=-]*$/.test(value) ? value : shellSingleQuoted(value);

// A mode-600 temp file with an unpredictable name, then rename: a crash never leaves a half
// file, and a leftover temp file is removed on failure.
function writePrivate(path, content) {
  mkdirSync(dirname(path), { recursive: true });
  const temp = `${path}.${randomBytes(6).toString('hex')}.tmp`;
  try {
    writeFileSync(temp, content, { mode: 0o600, flag: 'wx' });
    chmodSync(temp, 0o600);
    renameSync(temp, path);
  } catch (error) {
    rmSync(temp, { force: true });
    throw error;
  }
}

function tryLock(lock) {
  try {
    mkdirSync(lock);
  } catch {
    return false;
  }
  try {
    writeFileSync(join(lock, 'pid'), String(process.pid));
  } catch (error) {
    rmSync(lock, { recursive: true, force: true });
    throw error;
  }
  return true;
}

function holderIsGone(lock) {
  let pid;
  try {
    pid = Number(readFileSync(join(lock, 'pid'), 'utf8'));
  } catch {
    return false;
  }
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return false;
  } catch (error) {
    return error.code === 'ESRCH';
  }
}

// A takeover gate is held for microseconds; one older than this was left by a crashed run.
const GATE_STALE_MS = 10_000;

// Removes a lock whose holder is dead. Takers serialise on a gate directory and re-judge the
// holder inside it: a dead holder never removes its own lock and a live run only creates the
// lock while it is absent, so the lock judged dead cannot be swapped for a fresh one before the
// remove. Renaming the lock without the gate could move a fresh lock a rival had just taken.
function takeOverStale(lock) {
  const gate = `${lock}.takeover`;
  if (!tryLock(gate)) {
    let age;
    try {
      age = Date.now() - statSync(gate).mtimeMs;
    } catch {
      return;
    }
    if (age < GATE_STALE_MS) return;
    rmSync(gate, { recursive: true, force: true });
    if (!tryLock(gate)) return;
  }
  try {
    if (holderIsGone(lock)) rmSync(lock, { recursive: true, force: true });
  } finally {
    rmSync(gate, { recursive: true, force: true });
  }
}

// One run at a time per target: two concurrent runs would each generate and overwrite.
function withLock(path, action) {
  // A symlinked target (also a dangling one) is refused before any read, write or chmod.
  try {
    if (lstatSync(path).isSymbolicLink()) fail(`${path} is a symlink; refusing to use it`);
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
  }
  const lock = `${path}.lock`;
  mkdirSync(dirname(path), { recursive: true });
  if (!tryLock(lock)) {
    // A SIGKILL leaves the lock behind; its owner pid tells a dead holder from a live one. A lock
    // without a readable pid (empty, garbage, zero) is treated as held.
    if (holderIsGone(lock)) takeOverStale(lock);
    if (!tryLock(lock))
      fail(`${lock} exists: another run is in progress (remove the directory if none is)`);
  }
  // fail() exits the process, so the lock is released on exit rather than in a finally.
  process.on('exit', () => rmSync(lock, { recursive: true, force: true }));
  action();
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
    const last = lastMatch(content, name);
    content = last
      ? `${content.slice(0, last.index)}${line}${content.slice(last.index + last[0].length)}`
      : `${content.replace(/\n*$/, '\n')}${line}\n`;
    written.push(name);
  };

  for (const rule of rules) {
    if (rule.kind === 'random-hex' && missing(rule.name)) append(rule.name, hex());
    else if (rule.kind === 'random-base64' && missing(rule.name)) append(rule.name, base64());
  }
  const port = valueOf(content, 'EVIDGATE_DB_PORT') || '5432';
  if (!/^\d{1,5}$/.test(port)) fail('EVIDGATE_DB_PORT is not a port number');
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
    append(
      rule.name,
      shellSingleQuoted(pairs.map(([key, value]) => `${key}=${dsnValue(value)}`).join(';')),
    );
  }
  // The committed local-only identity: an older file that lacks it takes the template's value.
  const templateContent = existsSync(template) ? readFileSync(template, 'utf8') : '';
  for (const rule of rules) {
    const committed = valueOf(templateContent, rule.name);
    // Template values are plain strings; the quoting only guards spaces, quotes and shell syntax.
    if (rule.kind === 'template-provided' && missing(rule.name) && committed)
      append(rule.name, shellValue(committed));
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
  else chmodSync(file, 0o600);
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
  else chmodSync(file, 0o600);
  process.stdout.write(
    added.length === 0
      ? `secret-placeholders: ${env} ${file}: nothing to add\n`
      : `secret-placeholders: ${env} wrote empty entries to ${file} for: ${added.join(', ')}\n`,
  );
}

const args = parseArgs(process.argv.slice(2));
const rules = loadRules(args.root);
if (args.env === 'dev') {
  const file = args.file ?? join(args.root, '.env.dev');
  withLock(file, () =>
    generateDev(args.root, rules, file, args.template ?? join(args.root, '.env.dev.template')),
  );
} else {
  const file = args.file ?? join(args.root, `.env.${args.env}.secrets`);
  withLock(file, () => generateRemote(args.root, rules, args.env, file));
}
