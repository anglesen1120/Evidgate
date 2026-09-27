#!/usr/bin/env node
// 10.1.1-T1.1 environment-configuration lint (story 10.1.1, AC1/AC2).
//
// The `EVIDGATE_*` surface is declared once in infra/config-matrix.json and
// consumed per environment by infra/<env>/environment.json. This script keeps
// that declaration honest in both directions; it never carries a hand-written
// key list.
//
// Consumption is discovered from:
//   src/**/*.cs           options classes bound to an `EVIDGATE:<Section>`
//                         section (with one level of `List<T>` element keys),
//                         `const string` key constants (MigratorOptions,
//                         WorkerSchedDataSource), and every `EVIDGATE_…` literal
//                         left in non-comment code — an undeclared literal is an
//                         error, so a new key cannot enter the code without a
//                         matrix row;
//   .env.dev.template     the dev environment's declared configuration;
//   infra/compose.dev.yml the dev stack's own variables.
// Test-harness and CI-tooling switches (`EVIDGATE_S3PARITY__*`,
// `EVIDGATE_ISOLATION__*`, `EVIDGATE_VERIFY_SKIP_DOCKER`,
// `EVIDGATE_PARITY_INCLUDE_DIVERGENCE`) are deliberately out of scope: they
// configure one test run or one CI job, not an environment. The boundary is
// stated in docs/runbooks/environments.md.
//
// Checks (each failure prints "E <check>: <detail>"; exit 1 when any failed):
//   discovery   the parsed key set equals the matrix key set, in both
//               directions, and no `EVIDGATE_` literal in source is unparsed;
//   matrix      every derivable field of a row — consumers (from the csproj
//               reference graph), type, requiredAtBoot, constraints — matches
//               what the parser derived; per-environment required/source maps
//               are complete; a name that looks like a secret is secret;
//               requiredAtBoot implies required in every environment; a
//               forbiddenValues entry names a known environment and a value the
//               row's enum allows;
//   env         infra/<env>/environment.json exists for each of dev, staging,
//               prod; its `environment` matches its directory; every declared
//               key is in the matrix; every matrix key required in that
//               environment is declared; `origin` matches the matrix `source`
//               for that environment; a declared `value` is inside the row's
//               `constraints.enum` and never on the row's `forbiddenValues`
//               list for that environment; a secret key never carries a literal
//               value (00 §5.7 S3);
//   probes      liveness/readiness use the 00 §9.1 internal paths with sane
//               thresholds, and the worker probe port equals the declared
//               EVIDGATE__WORKER__HEALTHPORT;
//   ingress     the provider-neutral ingress rule of 00 §9.1: /internal/* and
//               /metrics are on the internal-only list and never on the public
//               path list;
//   devstack    .env.dev.template and infra/dev/environment.json declare the
//               same key set, and a literal value declared in both is equal.
//
// usage: node scripts/check-env-matrix.mjs [--root <dir>] [--print-discovered] [--self-test]
//   --root <dir>        lint another tree (used by the --self-test fixtures)
//   --print-discovered  print the discovered keys as JSON and exit 0 (authoring aid)
//   --self-test         build fixture trees under a temp dir and assert the pass
//                       and the failure modes through the real CLI
import { execFileSync } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const SELF = fileURLToPath(import.meta.url);
const DEFAULT_ROOT = join(dirname(SELF), '..');

/** 00 §9.1: the operational paths that must never be on the public ingress. */
const INTERNAL_PATHS = ['/internal/*', '/metrics'];
/** 00 §9.1: the operational paths themselves, per process. */
const PROBE_PATHS = {
  api: { liveness: '/internal/health/live', readiness: '/internal/health/ready' },
  worker: { liveness: '/internal/health/live', readiness: '/internal/health/ready' },
};
const PROCESSES = ['api', 'worker', 'migrator'];
const ORIGINS = ['env-file', 'environment', 'secret-store', 'repo-file', 'default'];
const SCOPES = ['runtime', 'dev-stack'];
const ENTRY_POINTS = {
  'Evidgate.Api': 'api',
  'Evidgate.Worker': 'worker',
  'Evidgate.Migrator': 'migrator',
};
const SECRET_NAME_RE = /(?:^|_)(?:PASSWORD|SECRET|KEY|CONNECTIONSTRING|CREDENTIALS?|TOKEN)(?:$|_)/;
const IGNORED_DIRS = new Set(['.git', 'bin', 'obj', 'node_modules', 'dist']);

// --------------------------------------------------------------------------
// helpers
// --------------------------------------------------------------------------

const errors = [];
const fail = (check, message) => errors.push(`E ${check}: ${message}`);

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

/** Tracked + untracked-not-ignored files (git), else a filesystem walk. */
function listFiles(root) {
  let listing;
  try {
    listing = execFileSync('git', ['-C', root, 'ls-files', '-c', '-o', '--exclude-standard'], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch {
    listing = walk(root).join('\n');
  }
  return listing
    .split('\n')
    .filter(Boolean)
    .filter((p) => !p.split('/').some((segment) => IGNORED_DIRS.has(segment)))
    .map((p) => join(root, p))
    .filter((p) => existsSync(p) && statSync(p).isFile());
}

function walk(dir, base = dir, acc = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (IGNORED_DIRS.has(entry.name)) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) walk(full, base, acc);
    else acc.push(relative(base, full));
  }
  return acc;
}

/**
 * C# text with comments removed. A `//` inside a string literal (the OTLP
 * example URL, for instance) is kept, so a literal is not truncated mid-value.
 */
function stripComments(text) {
  const lines = [];
  let inBlock = false;
  for (const raw of text.split('\n')) {
    let line = '';
    let i = 0;
    let inString = false;
    while (i < raw.length) {
      const two = raw.slice(i, i + 2);
      if (inBlock) {
        if (two === '*/') {
          inBlock = false;
          i += 2;
        } else i += 1;
        continue;
      }
      if (inString) {
        line += raw[i];
        if (raw[i] === '\\' && i + 1 < raw.length) {
          line += raw[i + 1];
          i += 2;
          continue;
        }
        if (raw[i] === '"') inString = false;
        i += 1;
        continue;
      }
      if (two === '/*') {
        inBlock = true;
        i += 2;
        continue;
      }
      if (two === '//') break;
      if (raw[i] === '"') inString = true;
      line += raw[i];
      i += 1;
    }
    lines.push(line);
  }
  return lines.join('\n');
}

/** `65_535` → 65535 (C# digit separators inside the Range attributes). */
const numberOf = (text) => Number.parseInt(text.replace(/_/g, ''), 10);

const sortedJson = (value) => JSON.stringify(value ?? {}, Object.keys(value ?? {}).sort());

/**
 * `EVIDGATE_…` tokens in a text file. The `__NAME__` placeholder padding of
 * .env.dev.template is stripped, so a placeholder yields the variable name.
 */
const tokens = (text) =>
  [...text.matchAll(/EVIDGATE_[A-Z0-9_]+/g)]
    .map((m) => m[0].replace(/_+$/, ''))
    .filter((t) => /[A-Z0-9]$/.test(t));

// --------------------------------------------------------------------------
// discovery: the project reference graph, so `consumers` is derived, not
// asserted
// --------------------------------------------------------------------------

function projectGraph(root) {
  const projects = new Map();
  for (const file of listFiles(root).filter((f) => f.endsWith('.csproj'))) {
    const name = basename(file, '.csproj');
    projects.set(
      name,
      [...readFileSync(file, 'utf8').matchAll(/ProjectReference\s+Include="([^"]+)"/g)].map((m) =>
        basename(m[1].replace(/\\/g, '/'), '.csproj'),
      ),
    );
  }
  return projects;
}

function consumersOf(graph, project) {
  const consumers = [];
  for (const [entryPoint, process] of Object.entries(ENTRY_POINTS)) {
    const seen = new Set();
    const stack = [entryPoint];
    let reaches = project === entryPoint;
    while (!reaches && stack.length > 0) {
      const current = stack.pop();
      if (seen.has(current)) continue;
      seen.add(current);
      for (const reference of graph.get(current) ?? []) {
        if (reference === project) reaches = true;
        stack.push(reference);
      }
    }
    if (reaches) consumers.push(process);
  }
  return consumers.sort();
}

/** The project that emits a source file: nearest ancestor directory with a .csproj. */
function projectOf(root, file) {
  let dir = dirname(file);
  while (dir.startsWith(root) && dir !== root) {
    const candidates = readdirSync(dir).filter((name) => name.endsWith('.csproj'));
    if (candidates.length > 0) return basename(candidates[0], '.csproj');
    dir = dirname(dir);
  }
  return basename(dir);
}

// --------------------------------------------------------------------------
// discovery: options classes, key constants and undeclared literals
// --------------------------------------------------------------------------

/** True when the declaration's default initializer already satisfies [Required]. */
function defaultSatisfiesRequired(tail) {
  return /=*\s*(?:"[^"]+"|'[^']+'|true|false|[\d_]+)\b/.test(tail) || /=\s*new\s/.test(tail);
}

function csType(type) {
  const t = type.replace(/\?$/, '').trim();
  if (t.startsWith('List<') || t.endsWith('[]')) return 'array';
  if (t === 'int' || t === 'long') return 'integer';
  if (t === 'bool') return 'boolean';
  return 'string';
}

function constraintsFrom(attributes, enums) {
  const constraints = {};
  for (const attribute of attributes) {
    const enumType = attribute.match(/EnumDataType\(\s*typeof\((\w+)\)/);
    if (enumType && enums?.has(enumType[1])) constraints.enum = [...enums.get(enumType[1])];
    const range = attribute.match(/Range\(\s*([0-9_]+)\s*,\s*([0-9_]+)/);
    if (range) {
      constraints.min = numberOf(range[1]);
      constraints.max = numberOf(range[2]);
    }
    const minLength = attribute.match(/MinLength\((\d+)[,)]/);
    if (minLength) constraints.minLength = numberOf(minLength[1]);
    const maxLength = attribute.match(/MaxLength\((\d+)[,)]/);
    if (maxLength) constraints.maxLength = numberOf(maxLength[1]);
    if (attribute.includes('Url(')) constraints.format = 'uri';
  }
  return constraints;
}

/**
 * Every binding declaration in `src/**\/*.cs`. Returns key -> derived facts
 * { type, requiredAtBoot, constraints, consumers }.
 */
function discoverFromSource(root, graph) {
  const sourceFiles = listFiles(root).filter(
    (f) => f.endsWith('.cs') && relative(root, f).startsWith(`src${sep}`),
  );
  const classes = new Map(); // class name -> { section, properties, consumers }
  const constants = [];
  const keys = new Map();
  const enums = new Map(); // enum name -> member names

  const record = (key, facts) => {
    const existing = keys.get(key);
    if (!existing) {
      keys.set(key, { key, fromSource: true, ...facts });
      return;
    }
    existing.requiredAtBoot ||= facts.requiredAtBoot;
    existing.constraints = { ...facts.constraints, ...existing.constraints };
    existing.consumers = [...new Set([...existing.consumers, ...facts.consumers])].sort();
    if (existing.type === 'string' && facts.type !== 'string') existing.type = facts.type;
  };

  for (const file of sourceFiles) {
    const text = stripComments(readFileSync(file, 'utf8'));
    const consumers = consumersOf(graph, projectOf(root, file));
    let current = null;
    let attributes = [];
    let currentEnum = null;

    for (const line of text.split('\n')) {
      const enumMatch = line.match(/^\s*(?:public|internal)?\s*enum\s+(\w+)/);
      if (enumMatch) {
        currentEnum = enumMatch[1];
        enums.set(currentEnum, []);
        continue;
      }
      if (currentEnum) {
        if (/^\s*\}/.test(line)) {
          currentEnum = null;
          continue;
        }
        const member = line.match(/^\s*(\w+)\s*(?:=|,)/);
        if (member) enums.get(currentEnum).push(member[1]);
        continue;
      }
      const classMatch = line.match(
        /^\s*(?:public|internal|protected|private)?\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+(\w+)/,
      );
      if (classMatch) {
        current = classMatch[1];
        if (!classes.has(current))
          classes.set(current, { section: null, properties: [], consumers });
        attributes = [];
        continue;
      }
      if (current === null) continue;
      const info = classes.get(current);

      const section = line.match(/SectionName\s*=\s*"EVIDGATE:(\w+)"/);
      if (section) {
        info.section = section[1].toUpperCase();
        continue;
      }

      const constant = line.match(/const string (\w+)\s*=\s*"(EVIDGATE_[A-Z0-9_]+)"/);
      if (constant) {
        constants.push({ name: constant[1], key: constant[2], className: current, consumers });
        continue;
      }

      const trimmed = line.trim();
      if (/^\[/.test(trimmed)) {
        attributes.push(trimmed);
        continue;
      }
      // A multi-line attribute ([EnumDataType( / typeof(X), / ErrorMessage = …)])
      // continues until the line that closes it.
      if (attributes.length > 0 && !attributes[attributes.length - 1].trimEnd().endsWith(']')) {
        attributes[attributes.length - 1] += ` ${trimmed}`;
        continue;
      }

      const property = line.match(
        /^\s*(?:public|internal)\s+(required\s+)?([\w.]+(?:\s*<[^>]+>)?\??(?:\[\])?)\s+(\w+)\s*\{\s*get;\s*(?:init;|set;)\s*\}/,
      );
      if (property && !line.includes('const ')) {
        info.properties.push({
          name: property[3],
          type: property[2].trim(),
          // The C# `required` keyword is never a boot guarantee: the
          // configuration binder sets members by reflection. [Required]
          // (ValidateDataAnnotations) fails the boot only when a missing value
          // leaves the member invalid - a non-empty default initializer
          // (`= "0.0.0.0"`, `= 8081`, `= true`) already satisfies it, while an
          // empty string or an empty collection does not.
          required:
            attributes.some((a) => a.includes('Required(')) &&
            !defaultSatisfiesRequired(line.slice(property.index + property[0].length)),
          constraints: constraintsFrom(attributes, enums),
        });
        attributes = [];
        continue;
      }
      if (trimmed !== '' && !/^[}\])/>;]/.test(trimmed) && !/^\w+\s*[={]/.test(trimmed))
        attributes = [];
    }
  }

  const addProperty = (key, property, consumers) => {
    const element =
      property.type.match(/^List<(\w+)>\??$/) ?? property.type.match(/^(\w+)\[\]\??$/);
    if (!element) {
      record(key, {
        type: csType(property.type),
        requiredAtBoot: property.required,
        constraints: property.constraints,
        consumers,
      });
      return;
    }
    const elementClass = classes.get(element[1]);
    if (!elementClass) {
      fail('discovery', `${key} is a list of an unknown element type ${element[1]}`);
      return;
    }
    for (const nested of elementClass.properties) {
      if (/^List</.test(nested.type) || /\[\]$/.test(nested.type)) {
        fail(
          'discovery',
          `${key}__<n> nests a list inside a list element (${element[1]}.${nested.name}); ` +
            'the key pattern is not derivable, so the options class must be flattened',
        );
        continue;
      }
      record(`${key}__<n>__${nested.name.toUpperCase()}`, {
        type: csType(nested.type),
        requiredAtBoot: nested.required,
        constraints: nested.constraints,
        consumers,
      });
    }
  };

  for (const info of classes.values()) {
    if (!info.section) continue;
    for (const property of info.properties) {
      addProperty(
        `EVIDGATE__${info.section}__${property.name.toUpperCase()}`,
        property,
        info.consumers,
      );
    }
  }

  // Explicit constants (MigratorOptions, WorkerSchedDataSource, …): the constant
  // name maps to the property it configures, with the …Variable/…Key suffix dropped.
  for (const constant of constants) {
    const info = classes.get(constant.className);
    const base = constant.name.replace(/(Variable|Key|Name)$/, '');
    const property = info?.properties.find((p) =>
      p.name.toLowerCase().startsWith(base.toLowerCase()),
    );
    record(constant.key, {
      type: property ? csType(property.type) : 'string',
      // A constant-keyed reader (MigratorOptions.FromEnvironment) validates by
      // hand, so the parser cannot see its requirement; the matrix states it and
      // names the enforcement site in `requiredBy`.
      requiredAtBoot: false,
      constraints: property ? property.constraints : {},
      consumers: info?.consumers ?? constant.consumers,
    });
  }

  return keys;
}

/** `EVIDGATE_…` literals left in non-comment source that no parser produced. */
function undeclaredLiterals(root, keys) {
  const found = [];
  for (const file of listFiles(root).filter(
    (f) => f.endsWith('.cs') && relative(root, f).startsWith(`src${sep}`),
  )) {
    const text = stripComments(readFileSync(file, 'utf8'));
    for (const m of text.matchAll(/EVIDGATE_[A-Z0-9_]+/g)) {
      if (matches(m[0], keys)) continue;
      const line = text.slice(0, m.index).split('\n').length;
      found.push(`${relative(root, file)}:${line}: ${m[0]}`);
    }
  }
  return found;
}

function matches(token, keys) {
  const normalized = token.replace(/\*+/g, '<n>').replace(/_+$/, '');
  for (const key of keys) {
    if (key === token || key === normalized) return true;
    const rest = key.slice(normalized.length);
    if (
      key.slice(0, normalized.length) === normalized &&
      (rest.startsWith('_') || rest.startsWith('<n>'))
    )
      return true;
  }
  return false;
}

/** The dev-stack surface: .env.dev.template + infra/compose.dev.yml. */
function discoverDevStack(root) {
  const keys = new Map();
  for (const rel of ['.env.dev.template', 'infra/compose.dev.yml']) {
    const file = join(root, rel);
    if (!existsSync(file)) continue;
    for (const key of tokens(readFileSync(file, 'utf8'))) {
      if (!keys.has(key)) keys.set(key, { key });
    }
  }
  return keys;
}

function discover(root) {
  const graph = projectGraph(root);
  const discovered = discoverFromSource(root, graph);
  for (const key of discoverDevStack(root).keys()) {
    const existing = discovered.get(key);
    if (existing) {
      existing.consumers = [...new Set([...existing.consumers, 'dev-stack'])].sort();
      continue;
    }
    discovered.set(key, {
      key,
      fromSource: false,
      type: 'string',
      requiredAtBoot: false,
      constraints: {},
      consumers: ['dev-stack'],
    });
  }
  return discovered;
}

// --------------------------------------------------------------------------
// matrix and per-environment files
// --------------------------------------------------------------------------

function checkMatrix(root, discovered, envs) {
  const path = join(root, 'infra/config-matrix.json');
  if (!existsSync(path)) {
    fail('matrix', 'infra/config-matrix.json is missing');
    return new Map();
  }
  let matrix;
  try {
    matrix = readJson(path);
  } catch (error) {
    fail('matrix', `infra/config-matrix.json is not valid JSON: ${error.message}`);
    return new Map();
  }
  if (matrix.version !== 1)
    fail('matrix', `version must be 1, found ${JSON.stringify(matrix.version)}`);
  if (JSON.stringify(matrix.environments) !== JSON.stringify(envs)) {
    fail(
      'matrix',
      `environments must be ${JSON.stringify(envs)}, found ${JSON.stringify(matrix.environments)}`,
    );
  }

  const rows = new Map();
  for (const row of matrix.keys ?? []) {
    if (typeof row.key !== 'string' || !/^EVIDGATE__?[A-Z0-9_]+(<n>[A-Z0-9_]*)*$/.test(row.key)) {
      fail('matrix', `invalid key ${JSON.stringify(row.key)}`);
      continue;
    }
    if (rows.has(row.key)) fail('matrix', `duplicate key ${row.key}`);
    rows.set(row.key, row);
    if (!SCOPES.includes(row.scope))
      fail('matrix', `${row.key}: scope must be one of ${SCOPES.join('/')}`);
    if (!Array.isArray(row.consumers) || row.consumers.length === 0) {
      fail('matrix', `${row.key}: consumers must be a non-empty array`);
    } else if (row.consumers.some((c) => !PROCESSES.includes(c) && c !== 'dev-stack')) {
      fail(
        'matrix',
        `${row.key}: consumers must be a process or "dev-stack", found ${JSON.stringify(row.consumers)}`,
      );
    }
    if (typeof row.type !== 'string') fail('matrix', `${row.key}: type is required`);
    if (typeof row.requiredAtBoot !== 'boolean')
      fail('matrix', `${row.key}: requiredAtBoot must be a boolean`);
    if (typeof row.secret !== 'boolean') fail('matrix', `${row.key}: secret must be a boolean`);
    if (typeof row.note !== 'string' || row.note.trim() === '')
      fail('matrix', `${row.key}: a note is required`);
    if (row.requiredBy !== undefined) {
      if (typeof row.requiredBy !== 'string') {
        fail('matrix', `${row.key}: requiredBy must be a repository-relative path`);
      } else {
        const file = join(root, row.requiredBy);
        if (!existsSync(file))
          fail('matrix', `${row.key}: requiredBy ${row.requiredBy} does not exist`);
        else if (!readFileSync(file, 'utf8').includes(row.key)) {
          fail('matrix', `${row.key}: requiredBy ${row.requiredBy} does not mention ${row.key}`);
        }
      }
    }
    if (SECRET_NAME_RE.test(row.key) && row.secret !== true) {
      fail('matrix', `${row.key} is named like a secret but is declared secret:false`);
    }
    // A per-environment banned value (W7-SEC-04): the row's own enum allows it,
    // but a named environment must not set it. EVIDGATE__ISOLATION__SELFCHECK
    // forbids Disabled in staging and prod this way, so the rule "never in a
    // deployment" is enforced rather than documented.
    if (row.forbiddenValues !== undefined) {
      if (
        row.forbiddenValues === null ||
        typeof row.forbiddenValues !== 'object' ||
        Array.isArray(row.forbiddenValues)
      ) {
        fail('matrix', `${row.key}: forbiddenValues must be an object keyed by environment`);
      } else {
        for (const [env, forbidden] of Object.entries(row.forbiddenValues)) {
          if (!envs.includes(env)) {
            fail(
              'matrix',
              `${row.key}: forbiddenValues names ${env}, which is not one of ${envs.join('/')}`,
            );
            continue;
          }
          if (
            !Array.isArray(forbidden) ||
            forbidden.length === 0 ||
            forbidden.some((v) => typeof v !== 'string')
          ) {
            fail(
              'matrix',
              `${row.key}: forbiddenValues.${env} must be a non-empty array of strings`,
            );
            continue;
          }
          const allowed = row.constraints?.enum;
          const outside = allowed ? forbidden.filter((value) => !allowed.includes(value)) : [];
          if (outside.length > 0) {
            fail(
              'matrix',
              `${row.key}: forbiddenValues.${env} lists ${outside.join('/')}, which the enum ${allowed.join('/')} does not allow`,
            );
          }
        }
      }
    }
    for (const env of envs) {
      if (typeof row.required?.[env] !== 'boolean')
        fail('matrix', `${row.key}: required.${env} must be a boolean`);
      if (!ORIGINS.includes(row.source?.[env])) {
        fail('matrix', `${row.key}: source.${env} must be one of ${ORIGINS.join('/')}`);
      }
    }
    if (row.requiredAtBoot) {
      for (const env of envs) {
        if (row.required?.[env] !== true)
          fail('matrix', `${row.key} is required at boot, so required.${env} must be true`);
      }
    }
  }

  // set equality, both directions
  for (const key of discovered.keys()) {
    if (!rows.has(key))
      fail('discovery', `${key} is consumed by the code but missing from infra/config-matrix.json`);
  }
  for (const key of rows.keys()) {
    if (!discovered.has(key))
      fail('discovery', `${key} is declared in infra/config-matrix.json but nothing consumes it`);
  }

  // derived fields must match the declaration
  for (const [key, derived] of discovered.entries()) {
    const row = rows.get(key);
    if (!row) continue;
    if (JSON.stringify(row.consumers) !== JSON.stringify(derived.consumers)) {
      fail(
        'matrix',
        `${key}: consumers ${JSON.stringify(row.consumers)} but the reference graph says ${JSON.stringify(derived.consumers)}`,
      );
    }
    if (!derived.fromSource) continue;
    if (row.type !== derived.type)
      fail(
        'matrix',
        `${key}: type ${JSON.stringify(row.type)} but the binding declares ${JSON.stringify(derived.type)}`,
      );
    if (row.requiredAtBoot === false && derived.requiredAtBoot === true) {
      fail(
        'matrix',
        `${key}: requiredAtBoot false but ValidateDataAnnotations enforces [Required]`,
      );
    }
    // A requirement the parser cannot see (an explicit reader such as
    // MigratorOptions.FromEnvironment) names the site that enforces it; the site
    // must exist and must mention the key.
    if (
      row.requiredAtBoot === true &&
      derived.requiredAtBoot === false &&
      typeof row.requiredBy !== 'string'
    ) {
      fail(
        'matrix',
        `${key}: requiredAtBoot true needs requiredBy naming the site that fails the boot`,
      );
    }
    if (sortedJson(row.constraints) !== sortedJson(derived.constraints)) {
      fail(
        'matrix',
        `${key}: constraints ${sortedJson(row.constraints)} but the attributes say ${sortedJson(derived.constraints)}`,
      );
    }
  }

  return rows;
}

function checkEnvironment(root, env, rows) {
  const rel = `infra/${env}/environment.json`;
  const path = join(root, rel);
  if (!existsSync(path)) {
    fail('env', `${rel} is missing`);
    return null;
  }
  let file;
  try {
    file = readJson(path);
  } catch (error) {
    fail('env', `${rel} is not valid JSON: ${error.message}`);
    return null;
  }
  if (file.environment !== env)
    fail('env', `${rel}: environment must be "${env}", found ${JSON.stringify(file.environment)}`);
  if (typeof file.description !== 'string' || file.description.trim() === '')
    fail('env', `${rel}: description is required`);

  const config = file.config;
  if (config === null || typeof config !== 'object' || Array.isArray(config)) {
    fail('env', `${rel}: config must be an object keyed by variable name`);
    return file;
  }
  const declared = new Set(Object.keys(config));
  for (const key of declared) {
    const row = rows.get(key);
    if (!row) {
      fail('env', `${rel}: ${key} is not in infra/config-matrix.json`);
      continue;
    }
    const entry = config[key];
    if (entry === null || typeof entry !== 'object') {
      fail('env', `${rel}: ${key} needs an object with at least an origin`);
      continue;
    }
    if (entry.origin !== row.source?.[env]) {
      fail(
        'env',
        `${rel}: ${key} origin ${JSON.stringify(entry.origin)} but the matrix source.${env} is ${JSON.stringify(row.source?.[env])}`,
      );
    }
    if (row.secret && entry.value !== undefined) {
      fail('env', `${rel}: ${key} is a secret and must not carry a literal value (00 §5.7 S3)`);
    }
    if (entry.value !== undefined) {
      // A value the row itself declares must be one the option accepts, and must
      // not be one this environment forbids (W7-SEC-04): "Required or Disabled,
      // but never Disabled in staging or prod" is a matrix rule, not a note.
      if (Array.isArray(row.constraints?.enum) && !row.constraints.enum.includes(entry.value)) {
        fail(
          'env',
          `${rel}: ${key} declares value ${JSON.stringify(entry.value)} but the matrix enum is ${row.constraints.enum.join('/')}`,
        );
      }
      const forbidden = row.forbiddenValues?.[env];
      if (Array.isArray(forbidden) && forbidden.includes(entry.value)) {
        fail(
          'env',
          `${rel}: ${key} declares forbidden value ${JSON.stringify(entry.value)} in ${env} (the matrix forbids ${forbidden.join('/')} there)`,
        );
      }
    }
    if (entry.origin === 'default') {
      if (row.required?.[env] === true)
        fail('env', `${rel}: ${key} is required in ${env} but declared origin "default"`);
      if (entry.value !== undefined)
        fail('env', `${rel}: ${key} has origin "default" and a value; one of the two is wrong`);
    }
  }
  for (const [key, row] of rows.entries()) {
    if (row.required?.[env] === true && !declared.has(key)) {
      fail('env', `${rel}: ${key} is required in ${env} but is not declared in config`);
    }
  }

  checkProbes(rel, file);
  checkIngress(rel, file);
  return file;
}

function checkProbes(rel, file) {
  for (const process of ['api', 'worker']) {
    const probe = file.probes?.[process];
    if (!probe) {
      fail('probes', `${rel}: a probes.${process} block is required`);
      continue;
    }
    if (typeof probe.baseUrl !== 'string' || !/^https?:\/\//.test(probe.baseUrl)) {
      fail('probes', `${rel}: probes.${process}.baseUrl must be an http(s) URL`);
    }
    for (const kind of ['liveness', 'readiness']) {
      const entry = probe[kind];
      if (!entry) {
        fail('probes', `${rel}: probes.${process}.${kind} is required`);
        continue;
      }
      if (entry.path !== PROBE_PATHS[process][kind]) {
        fail(
          'probes',
          `${rel}: probes.${process}.${kind}.path must be ${PROBE_PATHS[process][kind]}, found ${JSON.stringify(entry.path)}`,
        );
      }
      for (const field of ['periodSeconds', 'timeoutSeconds', 'failureThreshold']) {
        if (!Number.isInteger(entry[field]) || entry[field] < 1) {
          fail('probes', `${rel}: probes.${process}.${kind}.${field} must be a positive integer`);
        }
      }
      if (
        Number.isInteger(entry.timeoutSeconds) &&
        Number.isInteger(entry.periodSeconds) &&
        entry.timeoutSeconds > entry.periodSeconds
      ) {
        fail(
          'probes',
          `${rel}: probes.${process}.${kind}.timeoutSeconds must not exceed periodSeconds`,
        );
      }
      if (kind === 'readiness') {
        for (const field of ['successThreshold', 'startupGraceSeconds']) {
          if (!Number.isInteger(entry[field]) || entry[field] < 1) {
            fail(
              'probes',
              `${rel}: probes.${process}.readiness.${field} must be a positive integer`,
            );
          }
        }
      }
    }
  }
  const healthPort = file.config?.['EVIDGATE__WORKER__HEALTHPORT']?.value;
  if (healthPort !== undefined) {
    const port = String(file.probes?.worker?.baseUrl ?? '').match(/:(\d+)(?:\/|$)/)?.[1];
    if (String(healthPort) !== port) {
      fail(
        'probes',
        `${rel}: probes.worker.baseUrl port ${port} does not match EVIDGATE__WORKER__HEALTHPORT=${healthPort}`,
      );
    }
  }
}

function checkIngress(rel, file) {
  const ingress = file.ingress;
  if (!ingress) {
    fail('ingress', `${rel}: an ingress block is required (00 §9.1)`);
    return;
  }
  const { publicPaths, internalOnlyPaths, operatorPaths } = ingress;
  if (!Array.isArray(publicPaths) || !Array.isArray(internalOnlyPaths)) {
    fail('ingress', `${rel}: ingress.publicPaths and ingress.internalOnlyPaths must be arrays`);
    return;
  }
  if (!Array.isArray(operatorPaths) || operatorPaths.length === 0) {
    fail('ingress', `${rel}: ingress.operatorPaths must name the operator ingress paths (00 §9.1)`);
  } else {
    for (const path of operatorPaths) {
      if (publicPaths.includes(path)) {
        fail('ingress', `${rel}: operator path ${path} cannot be on the public ingress (00 §9.1)`);
      }
    }
  }
  for (const path of INTERNAL_PATHS) {
    if (!internalOnlyPaths.includes(path))
      fail('ingress', `${rel}: ingress.internalOnlyPaths must include ${path}`);
    if (publicPaths.includes(path))
      fail('ingress', `${rel}: ingress.publicPaths must not include ${path}`);
  }
  for (const path of publicPaths) {
    if (path.startsWith('/internal') || path === '/metrics') {
      fail(
        'ingress',
        `${rel}: ${path} is an operational path and cannot be on the public ingress (00 §9.1)`,
      );
    }
  }
  if (typeof ingress.note !== 'string' || ingress.note.trim() === '')
    fail('ingress', `${rel}: ingress.note is required`);
}

function checkDevStack(root, devFile) {
  const template = join(root, '.env.dev.template');
  if (!existsSync(template)) return;
  const text = readFileSync(template, 'utf8');
  const templateKeys = new Set(tokens(text));
  const templateValues = new Map(
    [...text.matchAll(/^\s*(EVIDGATE_[A-Z0-9_]+)=(.*)$/gm)].map((m) => [m[1], m[2].trim()]),
  );
  const declared = new Set(Object.keys(devFile?.config ?? {}));
  for (const key of templateKeys) {
    if (!declared.has(key))
      fail('devstack', `.env.dev.template declares ${key} but infra/dev/environment.json does not`);
  }
  for (const [key, entry] of Object.entries(devFile?.config ?? {})) {
    if (entry?.origin !== 'env-file') continue;
    if (!templateKeys.has(key)) {
      fail(
        'devstack',
        `infra/dev/environment.json declares ${key} with origin "env-file" but .env.dev.template does not`,
      );
      continue;
    }
    const templateValue = templateValues.get(key);
    if (entry.value !== undefined && templateValue !== undefined && templateValue !== entry.value) {
      fail(
        'devstack',
        `${key}: infra/dev/environment.json says ${JSON.stringify(entry.value)} but .env.dev.template says ${JSON.stringify(templateValue)}`,
      );
    }
  }
}

// --------------------------------------------------------------------------
// lint
// --------------------------------------------------------------------------

function lint(root) {
  const envs = ['dev', 'staging', 'prod'];
  const discovered = discover(root);
  for (const [key, facts] of discovered) {
    if (facts.consumers.length === 0) {
      fail(
        'discovery',
        `${key} is not reachable from Evidgate.Api, Evidgate.Worker or Evidgate.Migrator`,
      );
    }
  }
  for (const literal of undeclaredLiterals(root, [...discovered.keys()])) {
    fail(
      'discovery',
      `${literal} — every EVIDGATE_ key in source must be in the parsed options/const set`,
    );
  }

  const rows = checkMatrix(root, discovered, envs);
  let devFile = null;
  for (const env of envs) {
    const file = checkEnvironment(root, env, rows);
    if (env === 'dev') devFile = file;
  }
  checkDevStack(root, devFile);

  return {
    keys: discovered.size,
    processKeys: [...discovered.values()].filter((k) => k.consumers.some((c) => c !== 'dev-stack'))
      .length,
  };
}

// --------------------------------------------------------------------------
// self-test: the pass and the failure modes through the real CLI
// --------------------------------------------------------------------------

function fixture(root) {
  const write = (rel, content) => {
    mkdirSync(dirname(join(root, rel)), { recursive: true });
    writeFileSync(join(root, rel), content);
  };
  const csproj = (references) =>
    `<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n` +
    (references.length === 0
      ? ''
      : `  <ItemGroup>\n${references.map((r) => `    <ProjectReference Include="${r}" />\n`).join('')}  </ItemGroup>\n`) +
    `</Project>`;
  write('src/BuildingBlocks/Demo/Evidgate.BuildingBlocks.Demo.csproj', csproj([]));
  write(
    'src/Evidgate.Api/Evidgate.Api.csproj',
    csproj([String.raw`..\BuildingBlocks\Demo\Evidgate.BuildingBlocks.Demo.csproj`]),
  );
  write(
    'src/Evidgate.Worker/Evidgate.Worker.csproj',
    csproj([String.raw`..\BuildingBlocks\Demo\Evidgate.BuildingBlocks.Demo.csproj`]),
  );
  write('src/Evidgate.Migrator/Evidgate.Migrator.csproj', csproj([]));
  write(
    'src/BuildingBlocks/Demo/DemoOptions.cs',
    `namespace Demo;\n` +
      `public enum DemoMode\n{\n` +
      `    Required,\n` +
      `    Disabled,\n` +
      `}\n` +
      `public sealed class DemoOptions\n{\n` +
      `    public const string SectionName = "EVIDGATE:Demo";\n` +
      `    [Required(ErrorMessage = "EVIDGATE__DEMO__SECRETVALUE is required")]\n` +
      `    public string SecretValue { get; init; } = string.Empty;\n` +
      `    [Range(1, 100, ErrorMessage = "EVIDGATE__DEMO__PAGESIZE must be 1..100")]\n` +
      `    public int PageSize { get; init; } = 50;\n` +
      `    [EnumDataType(typeof(DemoMode), ErrorMessage = "EVIDGATE__DEMO__MODE must be Required or Disabled")]\n` +
      `    public DemoMode Mode { get; init; } = DemoMode.Required;\n` +
      `    public List<RowOptions> Rows { get; init; } = [];\n` +
      `}\n` +
      `public sealed class RowOptions\n{\n` +
      `    public string Name { get; init; } = string.Empty;\n` +
      `}\n`,
  );
  write(
    '.env.dev.template',
    `EVIDGATE_DEMO_PORT=9999\nEVIDGATE_DEMO_PASSWORD=__EVIDGATE_DEMO_PASSWORD__\n` +
      `EVIDGATE__DEMO__SECRETVALUE=__EVIDGATE_DEMO_PASSWORD__\n`,
  );
  write(
    'infra/compose.dev.yml',
    `services:\n  demo:\n    environment:\n      EVIDGATE_DEMO_PORT: \${EVIDGATE_DEMO_PORT:-9999}\n`,
  );

  const row = (over) => ({
    scope: 'runtime',
    consumers: ['api', 'worker'],
    type: 'string',
    requiredAtBoot: false,
    constraints: {},
    secret: false,
    required: { dev: false, staging: false, prod: false },
    source: { dev: 'default', staging: 'default', prod: 'default' },
    note: 'fixture row',
    ...over,
  });
  write(
    'infra/config-matrix.json',
    JSON.stringify(
      {
        version: 1,
        environments: ['dev', 'staging', 'prod'],
        keys: [
          row({
            key: 'EVIDGATE__DEMO__SECRETVALUE',
            requiredAtBoot: true,
            secret: true,
            consumers: ['api', 'dev-stack', 'worker'],
            required: { dev: true, staging: true, prod: true },
            source: { dev: 'env-file', staging: 'secret-store', prod: 'secret-store' },
          }),
          row({
            key: 'EVIDGATE__DEMO__PAGESIZE',
            type: 'integer',
            constraints: { min: 1, max: 100 },
          }),
          row({
            key: 'EVIDGATE__DEMO__MODE',
            constraints: { enum: ['Required', 'Disabled'] },
            forbiddenValues: { staging: ['Disabled'], prod: ['Disabled'] },
            source: { dev: 'environment', staging: 'environment', prod: 'environment' },
          }),
          row({ key: 'EVIDGATE__DEMO__ROWS__<n>__NAME' }),
          row({
            key: 'EVIDGATE_DEMO_PORT',
            scope: 'dev-stack',
            consumers: ['dev-stack'],
            required: { dev: true, staging: false, prod: false },
            source: { dev: 'env-file', staging: 'default', prod: 'default' },
          }),
          row({
            key: 'EVIDGATE_DEMO_PASSWORD',
            scope: 'dev-stack',
            consumers: ['dev-stack'],
            secret: true,
            required: { dev: true, staging: false, prod: false },
            source: { dev: 'env-file', staging: 'default', prod: 'default' },
          }),
        ],
      },
      null,
      2,
    ) + '\n',
  );

  const probes = {
    api: {
      baseUrl: 'http://127.0.0.1:8080',
      liveness: {
        path: '/internal/health/live',
        periodSeconds: 10,
        timeoutSeconds: 1,
        failureThreshold: 3,
      },
      readiness: {
        path: '/internal/health/ready',
        periodSeconds: 5,
        timeoutSeconds: 3,
        failureThreshold: 3,
        successThreshold: 1,
        startupGraceSeconds: 60,
      },
    },
    worker: {
      baseUrl: 'http://127.0.0.1:8081',
      liveness: {
        path: '/internal/health/live',
        periodSeconds: 10,
        timeoutSeconds: 1,
        failureThreshold: 3,
      },
      readiness: {
        path: '/internal/health/ready',
        periodSeconds: 5,
        timeoutSeconds: 3,
        failureThreshold: 3,
        successThreshold: 1,
        startupGraceSeconds: 60,
      },
    },
  };
  const envFile = (env, config) => ({
    environment: env,
    description: `fixture ${env}`,
    config,
    probes,
    ingress: {
      publicPaths: ['/v1/*'],
      operatorPaths: ['/internal/operator/*'],
      internalOnlyPaths: ['/internal/*', '/metrics'],
      note: 'fixture rule',
    },
  });
  const devConfig = {
    EVIDGATE__DEMO__SECRETVALUE: { origin: 'env-file', from: '.env.dev' },
    EVIDGATE__DEMO__PAGESIZE: { origin: 'default' },
    EVIDGATE__DEMO__MODE: { origin: 'environment', value: 'Disabled' },
    'EVIDGATE__DEMO__ROWS__<n>__NAME': { origin: 'default' },
    EVIDGATE_DEMO_PORT: { origin: 'env-file', value: '9999' },
    EVIDGATE_DEMO_PASSWORD: { origin: 'env-file', from: '.env.dev' },
  };
  const remoteConfig = {
    EVIDGATE__DEMO__SECRETVALUE: { origin: 'secret-store', from: 'provider secret store' },
    EVIDGATE__DEMO__PAGESIZE: { origin: 'default' },
    EVIDGATE__DEMO__MODE: { origin: 'environment', value: 'Required' },
    'EVIDGATE__DEMO__ROWS__<n>__NAME': { origin: 'default' },
    EVIDGATE_DEMO_PORT: { origin: 'default' },
    EVIDGATE_DEMO_PASSWORD: { origin: 'default' },
  };
  for (const [env, config] of [
    ['dev', devConfig],
    ['staging', remoteConfig],
    ['prod', remoteConfig],
  ]) {
    write(`infra/${env}/environment.json`, JSON.stringify(envFile(env, config), null, 2) + '\n');
  }
}

function runCli(root) {
  try {
    return {
      code: 0,
      out: execFileSync(process.execPath, [SELF, '--root', root], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
      }),
    };
  } catch (error) {
    return { code: error.status ?? 1, out: `${error.stdout ?? ''}${error.stderr ?? ''}` };
  }
}

function selfTest() {
  const base = mkdtempSync(join(tmpdir(), 'env-matrix-'));
  const cases = [];
  const expect = (name, mutate, expected, marker) => {
    const root = join(base, name);
    mkdirSync(root, { recursive: true });
    fixture(root);
    if (mutate) mutate(root);
    cases.push({ name, expected, marker, ...runCli(root) });
  };

  expect('clean', null, 0, 'check-env-matrix: ');
  expect(
    'consumed-key-omitted-from-matrix',
    (root) => {
      const path = join(root, 'infra/config-matrix.json');
      const matrix = readJson(path);
      matrix.keys = matrix.keys.filter((row) => row.key !== 'EVIDGATE__DEMO__PAGESIZE');
      writeFileSync(path, JSON.stringify(matrix, null, 2));
    },
    1,
    'E discovery: EVIDGATE__DEMO__PAGESIZE is consumed by the code but missing',
  );
  expect(
    'required-key-absent-from-env-dir',
    (root) => {
      const path = join(root, 'infra/prod/environment.json');
      const file = readJson(path);
      delete file.config['EVIDGATE__DEMO__SECRETVALUE'];
      writeFileSync(path, JSON.stringify(file, null, 2));
    },
    1,
    'E env: infra/prod/environment.json: EVIDGATE__DEMO__SECRETVALUE is required in prod',
  );
  expect(
    'internal-path-on-public-ingress',
    (root) => {
      const path = join(root, 'infra/dev/environment.json');
      const file = readJson(path);
      file.ingress.publicPaths.push('/internal/health/live');
      writeFileSync(path, JSON.stringify(file, null, 2));
    },
    1,
    'E ingress: infra/dev/environment.json: /internal/health/live is an operational path',
  );
  expect(
    'secret-literal-in-env-file',
    (root) => {
      const path = join(root, 'infra/dev/environment.json');
      const file = readJson(path);
      file.config['EVIDGATE__DEMO__SECRETVALUE'].value = 'not-a-real-secret';
      writeFileSync(path, JSON.stringify(file, null, 2));
    },
    1,
    'E env: infra/dev/environment.json: EVIDGATE__DEMO__SECRETVALUE is a secret and must not carry a literal value',
  );
  expect(
    'new-literal-key-in-code',
    (root) => {
      const path = join(root, 'src/BuildingBlocks/Demo/DemoOptions.cs');
      writeFileSync(
        path,
        readFileSync(path, 'utf8').replace(
          'public int PageSize { get; init; } = 50;',
          'public int PageSize { get; init; } = 50;\n    public string NewKnob { get; init; } = string.Empty;',
        ),
      );
    },
    1,
    'E discovery: EVIDGATE__DEMO__NEWKNOB is consumed by the code but missing',
  );
  expect(
    'boot-requirement-unproven',
    (root) => {
      const path = join(root, 'src/BuildingBlocks/Demo/DemoOptions.cs');
      writeFileSync(
        path,
        readFileSync(path, 'utf8').replace(
          /\n    \[Required\(ErrorMessage = "EVIDGATE__DEMO__SECRETVALUE is required"\)\]/,
          '',
        ),
      );
    },
    1,
    'E matrix: EVIDGATE__DEMO__SECRETVALUE: requiredAtBoot true needs requiredBy',
  );
  expect(
    'required-by-missing-file',
    (root) => {
      const path = join(root, 'infra/config-matrix.json');
      const matrix = readJson(path);
      matrix.keys.find((row) => row.key === 'EVIDGATE__DEMO__SECRETVALUE').requiredBy =
        'src/Nowhere/Past.cs';
      writeFileSync(path, JSON.stringify(matrix, null, 2));
    },
    1,
    'E matrix: EVIDGATE__DEMO__SECRETVALUE: requiredBy src/Nowhere/Past.cs does not exist',
  );
  expect(
    'key-name-only-in-a-comment',
    (root) => {
      const path = join(root, 'src/BuildingBlocks/Demo/DemoOptions.cs');
      writeFileSync(
        path,
        `${readFileSync(path, 'utf8')}// EVIDGATE__DEMO__COMMENTEDKNOB is not consumption\n`,
      );
    },
    0,
    'check-env-matrix: ',
  );
  expect(
    'forbidden-value-in-prod',
    (root) => {
      const path = join(root, 'infra/prod/environment.json');
      const file = readJson(path);
      file.config['EVIDGATE__DEMO__MODE'].value = 'Disabled';
      writeFileSync(path, JSON.stringify(file, null, 2));
    },
    1,
    'E env: infra/prod/environment.json: EVIDGATE__DEMO__MODE declares forbidden value "Disabled" in prod',
  );
  expect(
    'value-outside-the-enum',
    (root) => {
      const path = join(root, 'infra/dev/environment.json');
      const file = readJson(path);
      file.config['EVIDGATE__DEMO__MODE'].value = 'Maybe';
      writeFileSync(path, JSON.stringify(file, null, 2));
    },
    1,
    'E env: infra/dev/environment.json: EVIDGATE__DEMO__MODE declares value "Maybe" but the matrix enum is Required/Disabled',
  );
  expect(
    'forbidden-value-outside-the-enum',
    (root) => {
      const path = join(root, 'infra/config-matrix.json');
      const matrix = readJson(path);
      matrix.keys.find((entry) => entry.key === 'EVIDGATE__DEMO__MODE').forbiddenValues.prod = [
        'disabled',
      ];
      writeFileSync(path, JSON.stringify(matrix, null, 2));
    },
    1,
    'E matrix: EVIDGATE__DEMO__MODE: forbiddenValues.prod lists disabled, which the enum Required/Disabled does not allow',
  );

  let failed = 0;
  for (const testCase of cases) {
    const exitOk = testCase.code === testCase.expected;
    const markerOk = testCase.marker === undefined || testCase.out.includes(testCase.marker);
    const ok = exitOk && markerOk;
    if (!ok) failed += 1;
    const detail = ok
      ? ''
      : ` — expected exit ${testCase.expected}${testCase.marker ? ` and ${JSON.stringify(testCase.marker)}` : ''}, ` +
        `got exit ${testCase.code}: ${testCase.out.trim().split('\n').slice(0, 6).join(' | ')}`;
    console.log(`${ok ? 'ok  ' : 'FAIL'}  ${testCase.name}${detail}`);
  }
  rmSync(base, { recursive: true, force: true });
  if (failed > 0) {
    console.error(`check-env-matrix --self-test: ${failed} of ${cases.length} cases failed`);
    process.exit(1);
  }
  console.log(`check-env-matrix --self-test: ${cases.length} cases passed`);
}

// --------------------------------------------------------------------------
// entry point
// --------------------------------------------------------------------------

const args = process.argv.slice(2);
let root = DEFAULT_ROOT;
let printDiscovered = false;
for (let i = 0; i < args.length; i += 1) {
  switch (args[i]) {
    case '--root':
      root = resolve(args[++i] ?? '');
      break;
    case '--print-discovered':
      printDiscovered = true;
      break;
    case '--self-test':
      selfTest();
      process.exit(0);
      break;
    case '-h':
    case '--help':
      console.log(
        'usage: node scripts/check-env-matrix.mjs [--root <dir>] [--print-discovered] [--self-test]',
      );
      process.exit(0);
      break;
    default:
      console.error(`check-env-matrix: unknown argument '${args[i]}'`);
      process.exit(2);
  }
}
root = join(root, '.');

if (printDiscovered) {
  console.log(
    JSON.stringify(
      [...discover(root).values()].sort((a, b) => a.key.localeCompare(b.key)),
      null,
      2,
    ),
  );
  process.exit(0);
}

const summary = lint(root);
if (errors.length > 0) {
  for (const error of errors) console.error(error);
  console.error(`check-env-matrix: ${errors.length} error(s)`);
  process.exit(1);
}
console.log(
  `check-env-matrix: ${summary.keys} keys (${summary.processKeys} consumed by a process) match infra/config-matrix.json ` +
    `and infra/{dev,staging,prod}/environment.json; ${INTERNAL_PATHS.join(' + ')} stay off the public ingress`,
);
