// Shared setup for the apps/web lint-boundary specs: the exported flat-config blocks of
// apps/web/lint/boundaries.cjs (loaded through Node's `require`, exactly as eslint.config.js
// loads them), the typescript-eslint parser, the lint helpers and the fixture paths.
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

export interface LintMessage {
  readonly ruleId: string | null;
  readonly severity: number;
  readonly message: string;
}

interface LinterInstance {
  verify(code: string, config: readonly unknown[], filename: string): LintMessage[];
}

interface LinterConstructor {
  new (options?: { cwd?: string }): LinterInstance;
}

export interface BoundariesBlock {
  readonly files?: readonly string[];
  readonly rules?: Readonly<Record<string, unknown>>;
}

const requireFromHere = createRequire(import.meta.url);

// Loaded through Node's `require`, not through an ESM import: the rules are CommonJS (like
// eslint.config.js itself) and ESLint is a Node-only package.
const BOUNDARIES_MODULE = '../../../apps/web/lint/boundaries.cjs';
const { Linter } = requireFromHere('eslint') as { Linter: LinterConstructor };
export const boundaries = requireFromHere(BOUNDARIES_MODULE) as readonly BoundariesBlock[];
const typescriptEslint = requireFromHere('typescript-eslint') as { parser: unknown };

const REPO_ROOT = fileURLToPath(new URL('../../../', import.meta.url));

// The parser is the only thing the typed blocks of eslint.config.js contribute to these
// rules, so a bare `Linter` needs it supplied explicitly.
const PARSER_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: {
    ecmaVersion: 'latest',
    sourceType: 'module',
    parser: typescriptEslint.parser,
  },
};

export const HTTP_OUTSIDE_CORE = 'evidgate/no-http-outside-core';
// The global half of the HTTP boundary is ESLint's own rule (see boundaries.cjs).
export const HTTP_GLOBALS = 'no-restricted-globals';
export const WIRE_DTO = 'evidgate/no-hand-written-wire-dto';
export const FEATURE_BOUNDARY = 'evidgate/no-feature-cross-import';
export const BYPASS_TRUST = 'evidgate/no-bypass-security-trust';
export const SECRET_FIELD = 'evidgate/no-secret-field-in-store';

export const FEATURE_FILE = 'apps/web/src/app/features/runs/x.ts';
export const CORE_HTTP_FILE = 'apps/web/src/app/core/http/x.ts';
export const CORE_DATA_FILE = 'apps/web/src/app/core/data/x.ts';
export const CORE_FILE = 'apps/web/src/app/core/routes.ts';
export const SHARED_FILE = 'apps/web/src/app/shared/x.ts';
export const APP_CONFIG_FILE = 'apps/web/src/app/app.config.ts';

// The Settings feature is the one folder where a store may hold metadata only, so a
// secret-shaped field name fails lint there and nowhere else.
export const SETTINGS_SECRETS_FILE = 'apps/web/src/app/features/settings/secrets/secrets.store.ts';
const SECRET_FIXTURE_DIR = new URL(
  '../../../apps/web/lint/fixtures/no-secret-field-in-store/',
  import.meta.url,
);

/**
 * A fixture's source text. The two fixtures are `.ts.txt` files, not `.ts`: eslint.config.js's
 * block for every `.ts` under apps/web types them against apps/web/tsconfig.json, which includes
 * only `src`, so a deliberate violation named `.ts` would fail the lint job instead of this spec.
 */
export function secretFixture(name: string): string {
  return readFileSync(new URL(name, SECRET_FIXTURE_DIR), 'utf8');
}

/**
 * The fixture directory as a filesystem path, and one fixture inside it as an absolute filename.
 * A `templateUrl` is resolved against the REAL directory of the file being linted, so the
 * component fixture is linted under a path inside this directory and the `.html.txt` file it
 * names sits next to it.
 */
const SECRET_FIXTURE_PATH = fileURLToPath(SECRET_FIXTURE_DIR);

export function secretFixturePath(name: string): string {
  return join(SECRET_FIXTURE_PATH, name);
}

export function lint(code: string, filename: string): readonly LintMessage[] {
  return new Linter({ cwd: REPO_ROOT }).verify(code, [PARSER_BLOCK, ...boundaries], filename);
}

export function ruleIds(code: string, filename: string): readonly (string | null)[] {
  return lint(code, filename).map((message) => message.ruleId);
}

// The allow-list is a property of the bypass rule itself, and the exported blocks only match
// `apps/web/src/**/*.ts`, so the filenames a file must not hide behind — an absolute path from
// a checkout directory named like the bridge, a Windows path — cannot be expressed through
// them. ESLint resolves a filename against `cwd` before it matches `files`, so the filesystem
// root is the only `cwd` from which an out-of-tree absolute path matches a block at all.
const BYPASS_RULE_MODULE = '../../../apps/web/lint/rules/no-bypass-security-trust.cjs';
const bypassRule = requireFromHere(BYPASS_RULE_MODULE) as unknown;
const FILESYSTEM_ROOT = fileURLToPath(new URL('/', import.meta.url));
const RULE_ONLY_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: { ecmaVersion: 'latest', sourceType: 'module', parser: typescriptEslint.parser },
  plugins: { evidgate: { rules: { 'no-bypass-security-trust': bypassRule } } },
  rules: { 'evidgate/no-bypass-security-trust': 'error' },
};

export function ruleIdsAt(code: string, filename: string): readonly (string | null)[] {
  return new Linter({ cwd: FILESYSTEM_ROOT })
    .verify(code, [RULE_ONLY_BLOCK], filename)
    .map((message) => message.ruleId);
}

// The secret rule is also applied on its own, from the filesystem root. A `templateUrl` is
// resolved against the REAL directory of the file being linted, and the registered Settings
// block only matches `apps/web/src/app/features/settings/**/*.ts`, so a fixture directory cannot
// be reached through it — an absolute filename can only match a `**/*.ts` block. The
// registration is pinned by the registration spec, which is what keeps this a test seam and not
// a second way to run the rule.
const SECRET_RULE_MODULE = '../../../apps/web/lint/rules/no-secret-field-in-store.cjs';
const secretRule = requireFromHere(SECRET_RULE_MODULE) as unknown;
const SECRET_RULE_ONLY_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: { ecmaVersion: 'latest', sourceType: 'module', parser: typescriptEslint.parser },
  plugins: { evidgate: { rules: { 'no-secret-field-in-store': secretRule } } },
  rules: { 'evidgate/no-secret-field-in-store': 'error' },
};

export function lintSecretAt(code: string, filename: string): readonly LintMessage[] {
  return new Linter({ cwd: FILESYSTEM_ROOT }).verify(code, [SECRET_RULE_ONLY_BLOCK], filename);
}

/** The `data` each message interpolated, read back out of its text: `<prefix>"<name>".` */
export function quoted(prefix: string, messages: readonly LintMessage[]): readonly string[] {
  return messages.map((message) => {
    const start = message.message.indexOf(prefix);
    const from = start < 0 ? -1 : message.message.indexOf('"', start + prefix.length);
    const to = from < 0 ? -1 : message.message.indexOf('"', from + 1);
    return from < 0 || to < 0 ? '' : message.message.slice(from + 1, to);
  });
}
