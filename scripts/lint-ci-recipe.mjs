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
//                               ingest-token literal (`egt_<12 alnum>_…`) in any
//                               string; no literal value and no `vars.*`
//                               expression under an `env:` key or step `with:`
//                               input whose name says
//                               token/secret/password/credential/api-key
//                               (empty, true/false and digits are allowed); no
//                               credential-carrying CLI flag (`--token`,
//                               `--ingest-token`/`--ingest_token`, `--password`,
//                               `--pass`, `--secret`, `--api-key`, `--auth`,
//                               `--bearer`, `--pat`, `--jwt`, `--credential`, …
//                               with `=` or a following value, or `-t`/`-k` with
//                               a value on an `evidgate` command) in a `run:`
//                               script or a step's `with.args`; no
//                               `EVIDGATE_*TOKEN*` shell assignment or
//                               command-file line unless its value is a variable
//                               masked earlier in the same script.
//
// Text heuristics that remain, and their limits (everything above is
// structural; only `run:` script bodies and expression strings are text):
//   * Expressions: `${{ … }}` is found with a linear, quote-aware scan: a `}}`
//     inside a '…' string literal does not end it, and an unterminated `${{`
//     takes the rest of the string. An `if:` value without `${{` is read as one
//     bare expression. The `secrets` identifier is matched as a word not
//     preceded by `.`; a string literal containing the word "secrets" inside an
//     expression is a (deliberate, fail-closed) false positive.
//   * R4 shell heuristics. Only bash/sh bodies are analysed: a `run:` step
//     whose effective shell (step `shell:`, else job, else workflow
//     `defaults.run.shell`) is anything else is refused, and so is a step with
//     no shell set whose `runs-on` is not a literal `ubuntu-*`/`macos-*` label
//     (or a self-hosted label set naming linux/macos): a matrix or expression
//     runner may be Windows, whose default shell is pwsh. A script with a
//     logical line over MAX_LINE characters is refused unread. Scripts are
//     split into logical lines (backslash continuations joined), then into
//     command segments on `;`, `&&`, `||`, `|`, `&` outside quotes/`$(…)`/
//     backticks, each remembering the operator before it; an unquoted
//     word-initial `#` ends the line. `${{ github.env }}` and its
//     output/state/path siblings are read as $GITHUB_ENV etc., and a variable
//     assigned exactly a command-file variable (`F=$GITHUB_ENV`, `F="${GITHUB_ENV}"`)
//     becomes one too; `${!…}` indirection and `::stop-commands::` (also
//     quote-split) are refused. A segment that mentions a command file
//     ($GITHUB_ENV / OUTPUT / STATE / PATH, bare, braced, quoted, or the bare
//     name as in `$(printenv GITHUB_ENV)`) passes only as `echo`/`printf` with a
//     `>`/`>>` redirection or a `cat` heredoc; anything else (`tee`, `cp`,
//     `dd of=`, `cmd >> $GITHUB_ENV`, an env-prefix `FOO=1 echo …`, a
//     pipeline) is refused, because its payload cannot be proven masked. In an
//     accepted write every command substitution, `secrets`/`steps` expression
//     and shell variable (except runner-set GITHUB_*/RUNNER_*) must have been
//     masked in an EARLIER segment by exactly `echo "::add-mask::$X"` (one
//     variable, `${{ … }}` or `$(…)`, optionally double-quoted), first on its
//     line or after `;`, not piped, not redirected and not inside an open
//     `if`/`case`/loop/`{ }`/`( )`/function body; assigning X again (`=` or
//     `+=`) drops its mask. A heredoc starts only at a `<<WORD` outside quotes
//     and `$(…)`; its body is data. A mask inside another string, a comment, a
//     heredoc body or the same segment does not count (brace groups and
//     subshells redirected as a whole are refused by the same rule, since their
//     segment starts with `}`/`)`). Not modelled: functions as callees, `eval`,
//     `source`, variables set by `read`, file-descriptor variables, other
//     interpreters fed by a heredoc (`python - <<EOF`), and
//     `actions/github-script` (`core.exportVariable`). A recipe that needs one
//     of those must be rewritten in the plain form; the lint's job is to keep
//     the published recipe simple enough to audit, not to prove arbitrary
//     shell safe.
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
// `key` alone is left out so `with: key: ${{ vars.CACHE_KEY }}` (actions/cache) stays clean.
const CREDENTIAL_NAME = /token|secret|password|passwd|credential|api[-_]?key|private[-_]?key/i;
// `EVIDGATE_…TOKEN…` names; two plain tests, so a long name cannot backtrack.
const isEvidgateTokenName = (n) => /^EVIDGATE_/i.test(n) && /TOKEN/i.test(n);
// Ingest-token wire format `egt_<12 alnum prefix>_<secret>` (IngestTokenFormat.cs).
const INGEST_TOKEN_LITERAL = /egt_[A-Za-z0-9]{12}_/;
const VARS_REF = /(?<![A-Za-z0-9_.-])vars\s*(?:\.|\[)/;
const CREDENTIAL_FLAG =
  /(?:^|[\s"'])--(?:[A-Za-z0-9]+[-_])*(?:token|password|passwd|pass|secret|api[-_]?key|credential|auth|bearer|pat|jwt)s?(?=[=\s"']|$)/i;
// Short credential flags on an `evidgate` command line (`-t <value>`, `-k=<value>`).
const SHORT_CREDENTIAL_FLAG = /(?:^|\s)-[tk](?:=\S|\s+[^-\s])/;
// A logical shell line longer than this is refused rather than scanned: it keeps
// every per-line scan bounded on hostile input (no recipe line comes close).
const MAX_LINE = 8192;
// `${{ github.env }}` & co. expand to the same paths; rewritten to `$GITHUB_*` before analysis.
const GITHUB_FILE_CONTEXT = /\$\{\{\s*github\s*\.\s*(env|output|state|path)\s*\}\}/gi;
const GITHUB_FILE_VARS = new Set(['GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_STATE', 'GITHUB_PATH']);
// Only bash/sh bodies are analysable; `bash -e {0}`-style custom bash commands count as bash.
const ANALYSABLE_SHELL = /^(?:bash|sh)(?:\s|$)/;

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

  for (const [jobId, job] of Object.entries(jobs)) {
    const jloc = `jobs.${jobId}`;
    if (!isObj(job)) {
      add(PARSE, `workflow shape: ${jloc} is not a mapping`);
      continue;
    }
    if (Object.hasOwn(job, 'uses')) checkUses(job.uses, jloc, add);
    if (Object.hasOwn(job, 'secrets')) checkJobSecrets(job.secrets, jloc, add);
    const jobShell = defaultShell(job) ?? defaultShell(wf);
    const posixRunner = knownPosixRunner(job['runs-on']);
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
      if (Object.hasOwn(step, 'run')) {
        const shell = step.shell ?? jobShell;
        if (typeof step.run !== 'string') {
          add(PARSE, `workflow shape: ${sloc}.run is not a string`);
        } else if (shell === undefined && !posixRunner) {
          add(
            R4,
            `${sloc} has no \`shell:\` and its \`runs-on\` is not a literal Linux/macOS label (an expression, matrix or Windows runner may default to pwsh) — only bash/sh run steps are analysable; set \`shell: bash\``,
          );
        } else if (
          shell !== undefined &&
          !(typeof shell === 'string' && ANALYSABLE_SHELL.test(shell.trim()))
        ) {
          add(
            R4,
            `${sloc} uses shell ${JSON.stringify(shell)} — only bash/sh run steps are analysable; rewrite it in bash`,
          );
        } else {
          checkRun(step.run, sloc, add);
        }
      }
      if (isObj(step.with)) checkVarsCredentials(step.with, `${sloc}.with`, add, { literal: true });
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

function defaultShell(scope) {
  const run = isObj(scope.defaults) && isObj(scope.defaults.run) ? scope.defaults.run : null;
  return run && Object.hasOwn(run, 'shell') ? run.shell : undefined;
}

// True only for a literal GitHub-hosted Linux/macOS label (`ubuntu-*`, `macos-*`) or a
// self-hosted label set that names `linux`/`macos`; an expression, a matrix value or
// any other label may resolve to Windows, whose default shell is pwsh.
function knownPosixRunner(runsOn) {
  let labels = isObj(runsOn) ? runsOn.labels : runsOn;
  if (typeof labels === 'string') labels = [labels];
  if (!Array.isArray(labels) || labels.length === 0) return false;
  if (!labels.every((l) => typeof l === 'string' && !l.includes('${{'))) return false;
  const ls = labels.map((l) => l.trim().toLowerCase());
  if (ls.some((l) => /^(?:ubuntu|macos)-/.test(l))) return true;
  return ls.includes('self-hosted') && ls.some((l) => l === 'linux' || l === 'macos');
}

// `vars.*` is plain configuration, readable by anyone with repository read access
// and printed unmasked in logs; it never carries a credential. In an `env:` map a
// credential-named key with a literal value (no expression) is a committed
// credential too.
function checkVarsCredentials(map, loc, add, { literal = false } = {}) {
  for (const [name, value] of Object.entries(map)) {
    if (!CREDENTIAL_NAME.test(name) || typeof value !== 'string') continue;
    const { exprs } = scanExpressions(value);
    if (exprs.some((e) => VARS_REF.test(e))) {
      add(
        R5,
        `${loc}.${name} feeds a credential from \`vars.*\` — configuration variables are not secret storage; mint a short-lived token at run time`,
      );
    } else if (literal && exprs.length === 0 && !/^\s*(?:|true|false|\d+)\s*$/i.test(value)) {
      add(
        R5,
        `${loc}.${name} sets a credential-named variable to a literal value — never commit a credential; mint a short-lived token at run time`,
      );
    }
  }
}

// ---------------------------------------------------------------------------
// R5 walk over every string scalar and key

// Splits `${{ … }}` expressions out of a string in one linear pass. A `}}` inside a
// single-quoted expression string literal (`''` escapes a quote) does not end the
// expression; an unterminated `${{` takes the rest of the string as its expression
// (fail-closed). `rest` is the string with every expression replaced by a space.
function scanExpressions(str) {
  const exprs = [];
  let rest = '';
  let pos = 0;
  for (;;) {
    const start = str.indexOf('${{', pos);
    if (start < 0) break;
    rest += str.slice(pos, start) + ' ';
    let k = start + 3;
    let end = -1;
    while (k < str.length) {
      if (str[k] === "'") {
        k++;
        while (k < str.length && !(str[k] === "'" && str[k + 1] !== "'"))
          k += str[k] === "'" ? 2 : 1;
        k++;
      } else if (str[k] === '}' && str[k + 1] === '}') {
        end = k;
        break;
      } else {
        k++;
      }
    }
    if (end < 0) {
      exprs.push(str.slice(start + 3));
      return { exprs, rest };
    }
    exprs.push(str.slice(start + 3, end));
    pos = end + 2;
  }
  return { exprs, rest: rest + str.slice(pos) };
}

const expressionsIn = (str) => scanExpressions(str).exprs;

function secretRefs(expr) {
  const bad = [];
  const re =
    /(?<![A-Za-z0-9_.-])secrets\b(?:\s*\.\s*([A-Za-z_][A-Za-z0-9_-]*)|\s*\[\s*(['"])([^'"]{0,256})\2\s*\])?/gi;
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
    if (INGEST_TOKEN_LITERAL.test(node)) {
      add(
        R5,
        `${path || '<root>'} contains an Evidgate ingest-token literal (egt_…) — never commit a token`,
      );
    }
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
    if (key === 'env') checkVarsCredentials({ [k]: v }, path, add, { literal: true });
    if (key === 'env' && isEvidgateTokenName(k)) {
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
  const { exprs: rawExprs, rest } = scanExpressions(text);
  const exprs = rawExprs.map(norm);
  const cmds = [];
  const vars = [];
  let m;
  const cmdRe = /\$\(((?:[^()]|\([^()]*\))*)\)|`([^`]*)`/g;
  while ((m = cmdRe.exec(rest)) !== null) cmds.push(norm(m[1] ?? m[2]));
  const varRe = /\$\{?([A-Za-z_][A-Za-z0-9_]*)/g;
  while ((m = varRe.exec(rest)) !== null) vars.push(m[1]);
  return { cmds, exprs, vars };
}

const derivedExpr = (e) => /(?<![A-Za-z0-9_.-])(?:secrets\b|steps\s*\.)/i.test(e);

// Split one logical line into command segments on `;`, `&&`, `||`, `|` and `&`
// outside quotes, `$(…)` and backticks; an unquoted `#` that starts a word ends
// the line (comment). Quote characters are kept so later scans see the text.
// Each segment carries `sep`, the operator before it ('' for the first one), and
// `heredoc`, the `<<WORD` match of an operator found outside quotes and `$(…)`
// (null when the segment starts no heredoc).
function segmentsOf(line) {
  const segs = [];
  let cur = '';
  let sep = '';
  let hd = -1; // index in `cur` of an unquoted, top-level `<<`
  let q = null; // "'", '"' or '`'
  let depth = 0;
  let arith = 0; // open bare `(( … ))`
  const last = () => (cur.length > 0 ? cur[cur.length - 1] : '');
  const push = () => {
    if (cur.trim()) {
      segs.push({ text: cur.trim(), sep, heredoc: hd >= 0 ? HEREDOC.exec(cur.slice(hd)) : null });
    }
    hd = -1;
  };
  for (let k = 0; k < line.length; k++) {
    const c = line[k];
    if (q) {
      cur += c;
      if (c === '\\' && q !== "'" && k + 1 < line.length) cur += line[++k];
      else if (c === q) q = null;
      continue;
    }
    if (c === '\\' && k + 1 < line.length) {
      cur += c + line[++k];
      continue;
    }
    if (c === "'" || c === '"' || c === '`') {
      q = c;
      cur += c;
      continue;
    }
    if (c === '$' && line[k + 1] === '(') {
      depth++;
      cur += '$(';
      k++;
      continue;
    }
    if (depth > 0) {
      if (c === '(') depth++;
      else if (c === ')') depth--;
      cur += c;
      continue;
    }
    // Bare arithmetic `(( … ))` (also `for (( …; …; … ))`): no separators and no
    // heredoc inside; `1<<N` is a shift.
    if (c === '(' && line[k + 1] === '(') {
      arith++;
      cur += '((';
      k++;
      continue;
    }
    if (arith > 0) {
      if (c === ')' && line[k + 1] === ')') {
        arith--;
        cur += '))';
        k++;
      } else cur += c;
      continue;
    }
    if (c === '#' && (cur === '' || /\s/.test(last()))) break;
    if (c === '<' && line[k + 1] === '<' && line[k + 2] !== '<' && last() !== '<' && hd < 0) {
      hd = cur.length;
    }
    if (c === ';' || c === '|' || c === '&') {
      // `>&2`, `2>&1`, `&>` are redirections, not separators.
      if (c === '&' && (last() === '<' || last() === '>' || line[k + 1] === '>')) {
        cur += c;
        continue;
      }
      let op = c;
      if ((c === '|' || c === '&') && line[k + 1] === c) {
        op = c + c;
        k++;
      }
      push();
      sep = op;
      cur = '';
      continue;
    }
    cur += c;
  }
  push();
  return segs;
}

// `<<WORD`, `<<-'WORD'`, `<<"WORD"`; `<<<` (here-string) is not a heredoc.
const HEREDOC = /(?<!<)<<(?!<)-?\s*(['"]?)([A-Za-z_][A-Za-z0-9_]*)\1/;
// A mask is credited only in this exact form: echo/printf whose single argument is
// `::add-mask::` followed by one variable, expression or command substitution.
const MASK_EXACT =
  /^(?:echo|printf)\s+(?:-[A-Za-z]+\s+)*("?)::add-mask::(\$[A-Za-z_][A-Za-z0-9_]*|\$\{[A-Za-z_][A-Za-z0-9_]*\}|\$\{\{[^}]*\}\}|\$\((?:[^()]|\([^()]*\))*\))\1$/;
const MASK_LIKE = /^(?:echo|printf)\b[^]*::add-mask::/;
const ASSIGN =
  /^(?:(?:export|local|readonly)\s+|declare\s+(?:-\w+\s+)*)?([A-Za-z_][A-Za-z0-9_]*)\+?=(.*)$/;
const FILE_ALIAS = /^(["']?)\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?\1$/;
const COMPOUND_OPEN = new Set(['if', 'while', 'until', 'for', 'case', 'select', '{']);
const COMPOUND_CLOSE = new Set(['fi', 'done', 'esac']);
// Net `(` minus `)` outside quoted strings (`$(…)` and `(( … ))` count zero).
const balance = (s) => {
  let n = 0;
  for (const ch of s.replace(/'[^']*'|"(?:[^"\\]|\\.)*"/g, '')) {
    if (ch === '(') n++;
    else if (ch === ')') n--;
  }
  return n;
};
const VAR_REF = /\$\{?([A-Za-z_][A-Za-z0-9_]*)/g;
const WRITE_REF = /(?:>>?|\btee\s+(?:-a\s+|--append\s+)?)\s*"?\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?"?/g;
const BARE_FILE_NAME = /(?<![$\w{])GITHUB_(?:ENV|OUTPUT|STATE|PATH)\b/;

function checkRun(rawScript, loc, add) {
  // `${{ github.env }}` & co. expand to the command-file paths themselves.
  const script = rawScript.replace(GITHUB_FILE_CONTEXT, (_m, n) => `$GITHUB_${n.toUpperCase()}`);
  const lines = logicalLines(script);
  const long = lines.findIndex((l) => l.length > MAX_LINE);
  if (long >= 0) {
    add(
      R4,
      `${loc}.run line ${long + 1} is longer than ${MAX_LINE} characters — not analysed; split it`,
    );
    return;
  }
  // Quotes and backslashes are dropped first: `"::stop-comm""ands::"` prints the same command.
  if (script.replace(/["'\\]/g, '').includes('::stop-commands::')) {
    add(
      R4,
      `${loc}.run uses ::stop-commands::, which stops the runner from processing ::add-mask:: — not allowed in a recipe`,
    );
  }
  const masked = new Set();
  const fileVars = new Set(GITHUB_FILE_VARS);
  // A written variable must be masked earlier unless the runner itself sets it;
  // the lint cannot tell where an arbitrary shell variable's value came from.
  const needsMask = (v) => !fileVars.has(v) && !/^(?:GITHUB|RUNNER)_[A-Z0-9_]+$/.test(v);
  const maskedValue = (value) => {
    const r = refsIn(value);
    return (
      r.vars.length > 0 &&
      r.cmds.length === 0 &&
      r.exprs.length === 0 &&
      r.vars.every((v) => masked.has(`var:${v}`))
    );
  };
  // Set lookups instead of one alternation regex per segment: linear in the script.
  // A bare command-file name (`$(printenv GITHUB_ENV)`) counts as a mention too, so
  // such a write is refused rather than missed.
  const mentionsFile = (text) =>
    [...text.matchAll(VAR_REF)].some((m) => fileVars.has(m[1])) || BARE_FILE_NAME.test(text);
  const writesFile = (text) => [...text.matchAll(WRITE_REF)].some((m) => fileVars.has(m[1]));

  // Open compound commands (`if`, loops, `case`, `{ … }`, `( … )`, function
  // bodies): a mask inside one may not run, so it is not credited.
  let compound = 0;
  let subshell = 0;
  for (let i = 0; i < lines.length; i++) {
    const lno = i + 1;
    const segs = segmentsOf(lines[i]);
    let heredocEnd = null;
    segs.forEach(({ text: seg, sep, heredoc }, s) => {
      const next = s + 1 < segs.length ? segs[s + 1].sep : '';
      const cmd = seg.split(/\s+/)[0];
      if (heredoc && heredocEnd === null) heredocEnd = heredoc[2];
      // Subshells are tracked by paren balance, only once one has opened, so a
      // `case` pattern `x)` does not close anything.
      if (cmd.startsWith('(') || subshell > 0) subshell = Math.max(0, subshell + balance(seg));
      if (COMPOUND_OPEN.has(cmd) || /\{$/.test(seg)) compound++;
      else if (COMPOUND_CLOSE.has(cmd) || /^\}/.test(cmd)) compound = Math.max(0, compound - 1);

      if (CREDENTIAL_FLAG.test(seg)) {
        add(
          R5,
          `${loc}.run line ${lno} passes a credential as a CLI argument (env-only injection, 00 §5.7 S3)`,
        );
      } else if (/(?:^|[\s/])evidgate(?:\s|$)/.test(seg) && SHORT_CREDENTIAL_FLAG.test(seg)) {
        add(
          R5,
          `${loc}.run line ${lno} passes a value with a short flag (-t/-k) to evidgate; credentials are env-only (00 §5.7 S3)`,
        );
      }

      if (seg.includes('${!')) {
        add(
          R4,
          `${loc}.run line ${lno} uses indirect expansion \`\${!…}\`, which can name a GitHub command file — not analysable; write the plain form`,
        );
        return;
      }

      // A mask counts only in its exact form, as its own command that always runs
      // (first in its line or after `;`) and whose output is not piped; it covers
      // later segments only.
      if (MASK_LIKE.test(seg) && !mentionsFile(seg)) {
        const exact = MASK_EXACT.exec(seg);
        if (
          exact &&
          compound === 0 &&
          subshell === 0 &&
          (sep === '' || sep === ';') &&
          next !== '|' &&
          next !== '&'
        ) {
          const r = refsIn(exact[2]);
          r.vars.forEach((v) => masked.add(`var:${v}`));
          r.exprs.forEach((e) => masked.add(`expr:${e}`));
          r.cmds.forEach((c) => masked.add(`cmd:${c}`));
        }
        return;
      }

      const assign = ASSIGN.exec(seg);
      if (assign) {
        const [, name, value] = assign;
        const alias = FILE_ALIAS.exec(value);
        if (alias && fileVars.has(alias[2])) {
          // Indirect target: `F=$GITHUB_ENV` makes `>> "$F"` a command-file write.
          fileVars.add(name);
          masked.delete(`var:${name}`);
          return;
        }
        // A new value is not masked, whatever the old one was.
        masked.delete(`var:${name}`);
        if (isEvidgateTokenName(name) && !maskedValue(value)) {
          add(
            R5,
            `${loc}.run line ${lno} sets ${name} from something other than a masked runtime value`,
          );
        }
      }

      if (!mentionsFile(seg)) return;
      if (!writesFile(seg) || !['echo', 'printf', 'cat'].includes(cmd)) {
        add(
          R4,
          `${loc}.run line ${lno} \`${cmd}\` touches or names a GitHub command file other than by echo/printf/cat-heredoc redirection to $GITHUB_ENV/OUTPUT/STATE/PATH; its payload cannot be proven masked — mask a variable and echo it (a bare GITHUB_ENV-style word, even in a message, counts)`,
        );
        return;
      }

      // Payload: heredoc body, or the segment minus the redirection target.
      const payload = [];
      if (heredoc) {
        for (let j = i + 1; j < lines.length && lines[j].trim() !== heredoc[2]; j++) {
          payload.push(lines[j]);
        }
      } else if (cmd === 'cat') {
        add(
          R4,
          `${loc}.run line ${lno} redirects the output of \`cat\` into a GitHub file; command output cannot be proven masked — mask it into a variable and echo that`,
        );
        return;
      } else {
        payload.push(seg.replace(WRITE_REF, (m0, n) => (fileVars.has(n) ? ' ' : m0)));
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
          if (needsMask(v) && !masked.has(`var:${v}`))
            add(
              R4,
              `${loc}.run line ${lno} writes $${v} to a GitHub file without ::add-mask:: of that variable in an earlier command`,
            );
        }
        for (const evg of p.matchAll(/\b(EVIDGATE_\w*)=(.*)$/gim)) {
          if (isEvidgateTokenName(evg[1]) && !maskedValue(evg[2])) {
            add(
              R5,
              `${loc}.run line ${lno} exports ${evg[1]} from something other than a masked runtime value`,
            );
          }
        }
      }
    });
    // A heredoc body is data, never commands: skip it (including the end word).
    if (heredocEnd !== null) {
      while (i + 1 < lines.length && lines[i + 1].trim() !== heredocEnd) i++;
      if (i + 1 < lines.length) i++;
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
    // Anything else (e.g. RangeError from extreme nesting) is still a refusal with a
    // report line, and the remaining files are still linted.
    return [
      { rule: PARSE, detail: `not analysable: ${err?.name ?? 'error'}: ${err?.message ?? err}` },
    ];
  }
}

// Workflow text is untrusted: control characters are escaped and long values cut, so a
// report line can never carry a newline, an ANSI sequence or a forged `::command::`.
function safe(s) {
  const t = String(s).replace(
    /[\x00-\x1f\x7f]/g,
    (c) => `\\x${c.charCodeAt(0).toString(16).padStart(2, '0')}`,
  );
  return t.length > 400 ? `${t.slice(0, 400)}…` : t;
}

function lintFiles(files) {
  let code = 0;
  for (const file of files) {
    let violations;
    try {
      violations = lintFile(file);
    } catch (err) {
      if (!(err instanceof IoFailure)) throw err;
      console.error(`${TOOL}: ${safe(file)}: ${safe(err.message)}`);
      code = 2;
      continue;
    }
    for (const v of violations)
      console.error(`${TOOL}: ${safe(file)}: ${v.rule}: ${safe(v.detail)}`);
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
