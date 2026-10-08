#!/usr/bin/env node
// 11.2.1-T1.2 customer CI recipe lint (story 11.2.1, AC1; FR-11-2 a–e, 00 §5.7 S3).
//
// Checks a GitHub Actions workflow against the five fork-safe rules the customer
// CI security guide (docs/security/customer-ci-recipe.md) publishes. The entry
// point is scripts/lint-ci-recipe.sh; this file does the parsing.
//
// usage: scripts/lint-ci-recipe.sh <workflow.yml>...
//        scripts/lint-ci-recipe.sh --self-test
//
// Output: one line per violation on stderr,
//   lint-ci-recipe: <file>: <rule-id>: <detail>
// Every violation of every file is reported (no stop at the first one).
// Exit 0: every file is clean. Exit 1: any violation, or a file that is not
// parseable / not unambiguous YAML (rule id `yaml-parse`). Exit 2: usage or IO
// error (missing file, not a regular file, over the 1 MiB size cap); exit 2 wins
// over exit 1 when both happen in one run.
//
// Parsing — structure is never scanned with regexes:
//   The `yaml` package (2.9.1, ADR-0002) parses every file as YAML 1.2 core
//   schema, so `on` stays a string key (YAML 1.1 would read it as boolean true;
//   a key that still resolves to `true` is treated as `on` defensively). Refused
//   as `yaml-parse`: any parser error or warning, duplicate keys
//   (`uniqueKeys`), more than one document, an empty document, any explicit tag
//   (`!foo`, `!!binary`, even `!!str` — a workflow never needs one), a
//   non-scalar map key, and alias expansion beyond MAX_ALIAS_COUNT. Merge keys
//   (`<<`) are resolved. The rules then run on the resolved plain data
//   (`toJS`), so anchors/aliases, merge keys, flow style and quoted keys reach
//   the rules exactly as GitHub would see them.
//
// Rules (rule ids are the contract with the guide and the fixtures):
//   R1-no-pull-request-target   `on:` (string, list or map form) names only
//                               push, pull_request, workflow_dispatch,
//                               merge_group; pull_request_target, workflow_run
//                               and every other event are refused.
//   R2-sha-pinned               every remote `uses:` (step action and job-level
//                               reusable workflow) is owner/repo[/path]@<40
//                               lowercase hex>; `./…` local refs pass;
//                               `docker://` needs `@sha256:<64 hex>`. (The
//                               `# vX.Y.Z` comment is not data after parsing;
//                               scripts/check-action-pins.sh enforces it for
//                               this repo's own workflows.)
//   R3-token-permissions        top-level `permissions:` is required and is `{}`,
//                               `read-all`, or a map whose values are only
//                               `read`/`none`; jobs may elevate per job.
//   R4-add-mask                 see "R4 shell heuristics" below.
//   R5-no-long-lived-credential no `secrets` context reference other than
//                               secrets.GITHUB_TOKEN in any `${{ }}` expression
//                               or `if:` condition (dot, bracket, toJSON(secrets)
//                               and any other use); no job `secrets: inherit`
//                               and no job `secrets:` map entry other than
//                               `${{ secrets.GITHUB_TOKEN }}`; no
//                               `EVIDGATE_*TOKEN*` key in any `env:` map; no
//                               credential-carrying CLI flag (`--token`,
//                               `--ingest-token`, `--password`, `--secret`,
//                               `--api-key`, `--credential`, … with `=` or a
//                               following value) in a `run:` script or a step's
//                               `with.args`; no `EVIDGATE_*TOKEN*` shell
//                               assignment or `$GITHUB_ENV` line unless its value
//                               is a variable masked earlier in the same script.
//
// Text heuristics that remain, and their limits (everything above is
// structural; only `run:` script bodies and expression strings are text):
//   * Expressions: `${{ … }}` is found with a non-greedy scan up to the first
//     `}}`; an `if:` value without `${{` is read as one bare expression. The
//     `secrets` identifier is matched as a word not preceded by `.`; a string
//     literal containing the word "secrets" inside an expression is a
//     (deliberate, fail-closed) false positive.
//   * R4 shell heuristics: scripts are read line by line (backslash
//     continuations joined, `#` comment lines skipped). A line writes to a
//     GitHub file when it redirects (`>`/`>>`) or `tee`s into
//     $GITHUB_ENV / $GITHUB_OUTPUT / $GITHUB_STATE (bare, braced or quoted); a
//     heredoc opened on that line makes its body the written payload. A value is
//     "derived" when it is a command substitution (`$(…)`, backticks), a
//     `${{ secrets.* }}` / `${{ steps.*.outputs.* }}` expression, a variable
//     assigned earlier from one of those (simple `NAME=…`, `export`/`local`/
//     `readonly`/`declare` forms, first assignment on the line only), a step/
//     job/workflow `env:` variable whose value is such an expression, or a
//     variable whose name matches token|secret|password|credential|key. Masked
//     means an earlier `::add-mask::` line named the same variable, expression
//     or command text. The output of any other command redirected straight
//     into one of the files (`cmd >> $GITHUB_ENV`) is derived and can never be
//     proven masked, so it is refused; only echo/printf/cat-heredoc writes can
//     pass. Not modelled: brace groups/subshells redirected as a whole
//     (`{ …; } >> $GITHUB_ENV`), functions, `eval`, loops, variables set by
//     `read`, non-bash `shell:` values and writes via a file descriptor
//     variable. A recipe that needs one of those must be rewritten in the
//     plain form; the lint's job is to keep the published recipe simple enough
//     to audit, not to prove arbitrary shell safe.
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isMap, isScalar, parseAllDocuments, visit } from 'yaml';

const TOOL = 'lint-ci-recipe';
const MAX_BYTES = 1024 * 1024;
const MAX_ALIAS_COUNT = 100;

const R1 = 'R1-no-pull-request-target';
const R2 = 'R2-sha-pinned';
const R3 = 'R3-token-permissions';
const R4 = 'R4-add-mask';
const R5 = 'R5-no-long-lived-credential';
const PARSE = 'yaml-parse';
const RULE_IDS = new Set([R1, R2, R3, R4, R5, PARSE]);

const ALLOWED_EVENTS = new Set(['push', 'pull_request', 'workflow_dispatch', 'merge_group']);
const REMOTE_USES = /^[A-Za-z0-9-]+\/[A-Za-z0-9._-]+(?:\/[^@\s]+)?@[0-9a-f]{40}$/;
const DOCKER_USES = /^docker:\/\/[^@\s]+@sha256:[0-9a-f]{64}$/;
const LOCAL_USES = /^\.\/\S*$/;
const SUSPICIOUS_NAME = /token|secret|password|credential|key/i;
const EVIDGATE_TOKEN_NAME = /^EVIDGATE_\w*TOKEN/i;
const CREDENTIAL_FLAG =
  /(?:^|[\s"'])--(?:[A-Za-z0-9]+-)*(?:token|password|passwd|secret|api-?key|credential)s?(?=[=\s"']|$)/i;
const GITHUB_FILE =
  /(?:>>?|\btee\s+(?:-a\s+|--append\s+)?)\s*"?\$\{?GITHUB_(?:ENV|OUTPUT|STATE)\}?"?/;
const GITHUB_FILE_VARS = new Set(['GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_STATE']);

// ---------------------------------------------------------------------------
// parsing

class ParseFailure extends Error {}

function parseWorkflow(text) {
  const docs = parseAllDocuments(text, {
    version: '1.2',
    schema: 'core',
    uniqueKeys: true,
    merge: true,
    prettyErrors: true,
  });
  const list = Array.isArray(docs) ? docs : [docs];
  if (list.length !== 1) {
    throw new ParseFailure(
      list.length === 0 ? 'empty file' : `${list.length} YAML documents; a workflow is exactly one`,
    );
  }
  const doc = list[0];
  const problems = [...doc.errors, ...doc.warnings];
  if (problems.length > 0) {
    throw new ParseFailure(problems.map((p) => p.message.split('\n')[0]).join('; '));
  }
  if (doc.contents === null) throw new ParseFailure('empty document');
  visit(doc, {
    Node(_key, node) {
      if (node.tag !== undefined) {
        throw new ParseFailure(`explicit tag ${node.tag} is refused${at(node)}`);
      }
    },
    Pair(_key, pair) {
      if (!isScalar(pair.key)) throw new ParseFailure(`non-scalar map key${at(pair.key)}`);
      // A plain `<<` is the merge key, resolved by `merge: true` (value stays undefined).
      if (pair.key.type === 'PLAIN' && pair.key.source === '<<') return;
      const v = pair.key.value;
      if (typeof v !== 'string' && v !== true) {
        throw new ParseFailure(`map key ${JSON.stringify(v)} is not a string${at(pair.key)}`);
      }
    },
  });
  if (!isMap(doc.contents)) throw new ParseFailure('top level is not a mapping');
  try {
    return doc.toJS({ maxAliasCount: MAX_ALIAS_COUNT });
  } catch (err) {
    throw new ParseFailure(err instanceof Error ? err.message : String(err));
  }
}

function at(node) {
  return node && node.range ? ` (offset ${node.range[0]})` : '';
}

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

// ---------------------------------------------------------------------------
// rules

function lintData(wf) {
  const out = [];
  const add = (rule, detail) => out.push({ rule, detail });

  // `on` — a key that resolved to boolean true reaches toJS as "true".
  const onKeys = ['on', 'true'].filter((k) => Object.hasOwn(wf, k));
  if (onKeys.length === 0) add(R1, 'workflow has no `on:` trigger');
  for (const k of onKeys) checkEvents(wf[k], add);

  // R3 top-level permissions.
  if (!Object.hasOwn(wf, 'permissions')) {
    add(R3, 'top-level `permissions:` is missing (the default GITHUB_TOKEN scope may be write)');
  } else {
    checkTopPermissions(wf.permissions, add);
  }

  const jobs = wf.jobs;
  if (!isObj(jobs)) {
    add(PARSE, 'workflow shape: `jobs:` is missing or not a mapping');
    return out;
  }

  const wfEnv = isObj(wf.env) ? wf.env : {};
  for (const [jobId, job] of Object.entries(jobs)) {
    const jloc = `jobs.${jobId}`;
    if (!isObj(job)) {
      add(PARSE, `workflow shape: ${jloc} is not a mapping`);
      continue;
    }
    if (Object.hasOwn(job, 'uses')) checkUses(job.uses, jloc, add);
    if (Object.hasOwn(job, 'secrets')) checkJobSecrets(job.secrets, jloc, add);
    const jobEnv = isObj(job.env) ? job.env : {};
    if (!Object.hasOwn(job, 'steps')) continue;
    if (!Array.isArray(job.steps)) {
      add(PARSE, `workflow shape: ${jloc}.steps is not a list`);
      continue;
    }
    job.steps.forEach((step, i) => {
      const sloc = `${jloc}.steps[${i}]${isObj(step) && typeof step.name === 'string' ? ` (${step.name})` : ''}`;
      if (!isObj(step)) {
        add(PARSE, `workflow shape: ${sloc} is not a mapping`);
        return;
      }
      if (Object.hasOwn(step, 'uses')) checkUses(step.uses, sloc, add);
      const stepEnv = isObj(step.env) ? step.env : {};
      if (Object.hasOwn(step, 'run')) {
        if (typeof step.run !== 'string') {
          add(PARSE, `workflow shape: ${sloc}.run is not a string`);
        } else {
          checkRun(step.run, { ...wfEnv, ...jobEnv, ...stepEnv }, sloc, add);
        }
      }
      if (
        isObj(step.with) &&
        typeof step.with.args === 'string' &&
        CREDENTIAL_FLAG.test(step.with.args)
      ) {
        add(
          R5,
          `${sloc}.with.args passes a credential as a CLI argument (env-only injection, 00 §5.7 S3)`,
        );
      }
    });
  }

  // R5 whole-document walk: expressions, `if:` conditions, env maps.
  walk(wf, '', null, add);
  return out;
}

function checkEvents(on, add) {
  const names = [];
  if (typeof on === 'string') names.push(on);
  else if (Array.isArray(on)) {
    for (const e of on) {
      if (typeof e === 'string') names.push(e);
      else add(R1, `\`on:\` list entry ${JSON.stringify(e)} is not an event name`);
    }
  } else if (isObj(on)) names.push(...Object.keys(on));
  else add(R1, `\`on:\` must be an event name, list or map, got ${JSON.stringify(on)}`);
  for (const n of names) {
    if (!ALLOWED_EVENTS.has(n)) {
      const why =
        n === 'pull_request_target' || n === 'workflow_run'
          ? 'runs with the base repository secrets and a write token next to untrusted fork code'
          : 'not on the fork-safe allowlist (push, pull_request, workflow_dispatch, merge_group)';
      add(R1, `trigger \`${n}\` is refused: ${why}`);
    }
  }
}

function checkTopPermissions(p, add) {
  if (p === 'read-all') return;
  if (isObj(p)) {
    for (const [scope, level] of Object.entries(p)) {
      if (level !== 'read' && level !== 'none') {
        add(
          R3,
          `top-level permissions.${scope}: ${JSON.stringify(level)} — the default must be read/none; elevate per job`,
        );
      }
    }
    return;
  }
  add(
    R3,
    `top-level permissions: ${JSON.stringify(p)} — must be {}, read-all or a read/none map; elevate per job`,
  );
}

function checkUses(uses, loc, add) {
  if (typeof uses !== 'string') {
    add(R2, `${loc}.uses is not a string: ${JSON.stringify(uses)}`);
    return;
  }
  if (LOCAL_USES.test(uses) || REMOTE_USES.test(uses) || DOCKER_USES.test(uses)) return;
  const hint = uses.startsWith('docker://')
    ? 'pin the image by @sha256:<64 hex>'
    : 'pin a full 40-hex commit SHA';
  add(R2, `${loc}.uses '${uses}' is not pinned — ${hint}`);
}

function checkJobSecrets(secrets, loc, add) {
  if (secrets === 'inherit') {
    add(
      R5,
      `${loc}.secrets: inherit hands every repository/organisation secret to the called workflow`,
    );
    return;
  }
  if (!isObj(secrets)) {
    add(R5, `${loc}.secrets must be absent; got ${JSON.stringify(secrets)}`);
    return;
  }
  for (const [name, value] of Object.entries(secrets)) {
    if (
      typeof value !== 'string' ||
      !/^\$\{\{\s*secrets\.GITHUB_TOKEN\s*\}\}$/i.test(value.trim())
    ) {
      add(
        R5,
        `${loc}.secrets.${name} passes a credential other than secrets.GITHUB_TOKEN to the called workflow`,
      );
    }
  }
}

// ---------------------------------------------------------------------------
// R5 walk over every string scalar and key

function expressionsIn(str) {
  const out = [];
  const re = /\$\{\{([\s\S]*?)\}\}/g;
  let m;
  while ((m = re.exec(str)) !== null) out.push(m[1]);
  return out;
}

function secretRefs(expr) {
  const bad = [];
  const re =
    /(?<![A-Za-z0-9_.-])secrets\b(?:\s*\.\s*([A-Za-z_][A-Za-z0-9_-]*)|\s*\[\s*(['"])([^'"]*)\2\s*\])?/gi;
  let m;
  while ((m = re.exec(expr)) !== null) {
    const name = m[1] ?? m[3];
    if (name !== undefined && name.toUpperCase() === 'GITHUB_TOKEN') continue;
    bad.push(m[0].trim());
  }
  return bad;
}

function walk(node, path, key, add) {
  if (typeof node === 'string') {
    const exprs = expressionsIn(node);
    if (key === 'if' && exprs.length === 0) exprs.push(node);
    for (const e of exprs) {
      for (const ref of secretRefs(e)) {
        add(
          R5,
          `${path || '<root>'} references \`${ref}\` — only secrets.GITHUB_TOKEN may be read`,
        );
      }
    }
    return;
  }
  if (Array.isArray(node)) {
    node.forEach((v, i) => walk(v, `${path}[${i}]`, key, add));
    return;
  }
  if (!isObj(node)) return;
  for (const [k, v] of Object.entries(node)) {
    const p = path ? `${path}.${k}` : k;
    for (const e of expressionsIn(k)) {
      for (const ref of secretRefs(e)) add(R5, `key ${p} references \`${ref}\``);
    }
    if (key === 'env' && EVIDGATE_TOKEN_NAME.test(k)) {
      add(
        R5,
        `${p} sets an Evidgate token in workflow env — mint a short-lived token at run time (masked) instead`,
      );
    }
    walk(v, p, k, add);
  }
}

// ---------------------------------------------------------------------------
// R4 (and the shell half of R5) over one `run:` script

function logicalLines(script) {
  const out = [];
  let buf = '';
  for (const raw of script.split('\n')) {
    if (raw.endsWith('\\')) {
      buf += raw.slice(0, -1) + ' ';
      continue;
    }
    out.push(buf + raw);
    buf = '';
  }
  if (buf) out.push(buf);
  return out;
}

const norm = (s) => s.replace(/\s+/g, ' ').trim();

function refsIn(text) {
  const cmds = [];
  const exprs = [];
  const vars = [];
  const exprRe = /\$\{\{([\s\S]*?)\}\}/g;
  let m;
  while ((m = exprRe.exec(text)) !== null) exprs.push(norm(m[1]));
  const rest = text.replace(exprRe, ' ');
  const cmdRe = /\$\(((?:[^()]|\([^()]*\))*)\)|`([^`]*)`/g;
  while ((m = cmdRe.exec(rest)) !== null) cmds.push(norm(m[1] ?? m[2]));
  const varRe = /\$\{?([A-Za-z_][A-Za-z0-9_]*)/g;
  while ((m = varRe.exec(rest)) !== null) vars.push(m[1]);
  return { cmds, exprs, vars };
}

const derivedExpr = (e) => /(?<![A-Za-z0-9_.-])(?:secrets\b|steps\s*\.)/i.test(e);

function checkRun(script, env, loc, add) {
  const masked = new Set();
  const derived = new Set();
  for (const [name, value] of Object.entries(env)) {
    if (typeof value === 'string' && expressionsIn(value).some(derivedExpr)) derived.add(name);
  }
  const isDerivedVar = (v) =>
    !GITHUB_FILE_VARS.has(v) && (derived.has(v) || SUSPICIOUS_NAME.test(v));
  const maskedValue = (value) => {
    const r = refsIn(value);
    return (
      r.vars.length > 0 &&
      r.cmds.length === 0 &&
      r.exprs.length === 0 &&
      r.vars.every((v) => masked.has(`var:${v}`))
    );
  };

  const lines = logicalLines(script);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s*#/.test(line) || line.trim() === '') continue;
    const lno = i + 1;

    if (CREDENTIAL_FLAG.test(line)) {
      add(
        R5,
        `${loc}.run line ${lno} passes a credential as a CLI argument (env-only injection, 00 §5.7 S3)`,
      );
    }

    const maskAt = line.indexOf('::add-mask::');
    if (maskAt >= 0) {
      const r = refsIn(line.slice(maskAt + '::add-mask::'.length));
      r.vars.forEach((v) => masked.add(`var:${v}`));
      r.exprs.forEach((e) => masked.add(`expr:${e}`));
      r.cmds.forEach((c) => masked.add(`cmd:${c}`));
      continue;
    }

    const assign =
      /^\s*(?:(?:export|local|readonly)\s+|declare\s+(?:-\w+\s+)*)?([A-Za-z_][A-Za-z0-9_]*)=(.*)$/.exec(
        line,
      );
    if (assign) {
      const [, name, value] = assign;
      const r = refsIn(value);
      if (r.cmds.length > 0 || r.exprs.some(derivedExpr) || r.vars.some(isDerivedVar))
        derived.add(name);
      if (EVIDGATE_TOKEN_NAME.test(name) && !maskedValue(value)) {
        add(
          R5,
          `${loc}.run line ${lno} sets ${name} from something other than a masked runtime value`,
        );
      }
    }

    if (!GITHUB_FILE.test(line)) continue;

    // Payload: heredoc body, or the line itself minus the redirection target.
    const payload = [];
    const heredoc = /<<-?\s*(['"]?)([A-Za-z_][A-Za-z0-9_]*)\1/.exec(line);
    if (heredoc) {
      const end = heredoc[2];
      while (i + 1 < lines.length && lines[i + 1].trim() !== end) payload.push(lines[++i]);
      if (i + 1 < lines.length) i++;
    } else {
      const cmd = line.trim().split(/\s+/)[0];
      if (!['echo', 'printf'].includes(cmd)) {
        add(
          R4,
          `${loc}.run line ${lno} redirects the output of \`${cmd}\` into a GitHub file; command output cannot be proven masked — mask it into a variable and echo that`,
        );
        continue;
      }
      payload.push(line.replace(GITHUB_FILE, ' '));
    }
    for (const p of payload) {
      const r = refsIn(p);
      for (const c of r.cmds) {
        if (!masked.has(`cmd:${c}`))
          add(
            R4,
            `${loc}.run line ${lno} writes command output \`$(${c})\` to a GitHub file without ::add-mask::`,
          );
      }
      for (const e of r.exprs) {
        if (derivedExpr(e) && !masked.has(`expr:${e}`))
          add(
            R4,
            `${loc}.run line ${lno} writes \${{ ${e} }} to a GitHub file without ::add-mask::`,
          );
      }
      for (const v of r.vars) {
        if (isDerivedVar(v) && !masked.has(`var:${v}`))
          add(
            R4,
            `${loc}.run line ${lno} writes derived value $${v} to a GitHub file without ::add-mask:: earlier in the script`,
          );
      }
      const evg = /\b(EVIDGATE_\w*TOKEN\w*)=(.*)$/i.exec(p);
      if (evg && !maskedValue(evg[2])) {
        add(
          R5,
          `${loc}.run line ${lno} exports ${evg[1]} from something other than a masked runtime value`,
        );
      }
    }
  }
}

// ---------------------------------------------------------------------------
// CLI

class IoFailure extends Error {}

function lintFile(file) {
  let st;
  try {
    st = statSync(file);
  } catch (err) {
    throw new IoFailure(`cannot read: ${err.code ?? err.message}`);
  }
  if (!st.isFile()) throw new IoFailure('not a regular file');
  if (st.size > MAX_BYTES)
    throw new IoFailure(`${st.size} bytes is over the ${MAX_BYTES}-byte cap`);
  let text;
  try {
    text = readFileSync(file, 'utf8');
  } catch (err) {
    throw new IoFailure(`cannot read: ${err.code ?? err.message}`);
  }
  try {
    return lintData(parseWorkflow(text));
  } catch (err) {
    if (err instanceof ParseFailure) return [{ rule: PARSE, detail: err.message }];
    throw err;
  }
}

function lintFiles(files) {
  let code = 0;
  for (const file of files) {
    let violations;
    try {
      violations = lintFile(file);
    } catch (err) {
      if (!(err instanceof IoFailure)) throw err;
      console.error(`${TOOL}: ${file}: ${err.message}`);
      code = 2;
      continue;
    }
    for (const v of violations) console.error(`${TOOL}: ${file}: ${v.rule}: ${v.detail}`);
    if (violations.length > 0 && code === 0) code = 1;
  }
  return code;
}

// --self-test: lint the published recipe (expect exit 0) and every fixture under
// negative/ and bypass/ through the real CLI. Each fixture's first line is
// `# expect: <rule-id>`; it must exit 1 reporting that rule, and a negative/
// fixture must report no other rule (one fixture per rule, AC1).
function selfTest() {
  const here = dirname(fileURLToPath(import.meta.url));
  const root = resolve(here, '..');
  const base = join(root, 'fixtures', 'ci-recipes');
  const entry = join(here, 'lint-ci-recipe.sh');
  const run = (file) => {
    try {
      execFileSync(entry, [file], { stdio: ['ignore', 'pipe', 'pipe'], encoding: 'utf8' });
      return { status: 0, stderr: '' };
    } catch (err) {
      return { status: err.status ?? 2, stderr: String(err.stderr ?? '') };
    }
  };
  const rulesOf = (stderr) =>
    new Set(
      stderr
        .split('\n')
        .map((l) => /^lint-ci-recipe: .*?: ((?:R[1-5]-[a-z-]+)|yaml-parse): /.exec(l)?.[1])
        .filter(Boolean),
    );
  let failed = 0;
  const report = (ok, file, detail) => {
    console.log(`${ok ? 'PASS' : 'FAIL'} ${relative(root, file)}${detail ? ` — ${detail}` : ''}`);
    if (!ok) failed++;
  };

  const good = join(base, 'github-fork-safe.yml');
  let goodExists = false;
  try {
    goodExists = statSync(good).isFile();
  } catch {
    goodExists = false;
  }
  if (!goodExists) report(false, good, 'recipe is missing');
  else {
    const r = run(good);
    report(
      r.status === 0,
      good,
      r.status === 0 ? 'exit 0' : `exit ${r.status}: ${r.stderr.trim()}`,
    );
  }

  for (const dir of ['negative', 'bypass']) {
    let names = [];
    try {
      names = readdirSync(join(base, dir))
        .filter((n) => /\.ya?ml$/.test(n))
        .sort();
    } catch {
      names = [];
    }
    if (names.length === 0) report(false, join(base, dir), 'no fixtures');
    for (const n of names) {
      const file = join(base, dir, n);
      const first = readFileSync(file, 'utf8').split('\n')[0];
      const expect = /^#\s*expect:\s*(\S+)\s*$/.exec(first)?.[1];
      if (!expect || !RULE_IDS.has(expect)) {
        report(false, file, 'first line is not `# expect: <rule-id>`');
        continue;
      }
      const r = run(file);
      const seen = rulesOf(r.stderr);
      const others = [...seen].filter((x) => x !== expect);
      let ok = r.status === 1 && seen.has(expect);
      if (dir === 'negative' && others.length > 0) ok = false;
      report(
        ok,
        file,
        `exit ${r.status}, expect ${expect}, reported ${[...seen].join(',') || 'none'}`,
      );
    }
  }
  console.log(
    failed === 0
      ? `${TOOL} --self-test: all fixtures behave`
      : `${TOOL} --self-test: ${failed} failure(s)`,
  );
  return failed === 0 ? 0 : 1;
}

function main(argv) {
  if (argv.length === 1 && argv[0] === '--self-test') return selfTest();
  if (argv.length === 0 || argv.some((a) => a.startsWith('--'))) {
    console.error(`usage: ${TOOL}.sh <workflow.yml>... | --self-test`);
    return 2;
  }
  return lintFiles(argv);
}

process.exitCode = main(process.argv.slice(2));
