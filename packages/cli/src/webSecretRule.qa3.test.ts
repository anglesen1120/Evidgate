import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

interface LintMessage {
  readonly ruleId: string | null;
}

interface LinterInstance {
  verify(code: string, config: readonly unknown[], filename: string): LintMessage[];
}

interface LinterConstructor {
  new (options?: { cwd?: string }): LinterInstance;
}

const requireFromHere = createRequire(import.meta.url);
const { Linter } = requireFromHere('eslint') as { Linter: LinterConstructor };
const typescriptEslint = requireFromHere('typescript-eslint') as { parser: unknown };
const secretRule = requireFromHere(
  '../../../apps/web/lint/rules/no-secret-field-in-store.cjs',
) as unknown;

const FILESYSTEM_ROOT = fileURLToPath(new URL('/', import.meta.url));
const SECRET_RULE_ONLY_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: { ecmaVersion: 'latest', sourceType: 'module', parser: typescriptEslint.parser },
  plugins: { evidgate: { rules: { 'no-secret-field-in-store': secretRule } } },
  rules: { 'evidgate/no-secret-field-in-store': 'error' },
};

// Virtual absolute paths: the rule only needs a `**/*.ts` match and the file's directory.
const DIR = '/qa3-virtual/settings/secrets/';

function flagged(code: string, filename: string): boolean {
  return new Linter({ cwd: FILESYSTEM_ROOT })
    .verify(code, [SECRET_RULE_ONLY_BLOCK], filename)
    .some((message) => message.ruleId?.endsWith('no-secret-field-in-store') === true);
}

// The template goes in a backtick literal so bracket-quoted reads (store['token']) stay valid TS.
function component(template: string, members = ''): string {
  return `import { Component, signal } from '@angular/core';
@Component({ selector: 'app-qa3', template: \`${template}\` })
export class Qa3Component {
${members}
}
`;
}

describe('no-secret-field-in-store negatives and positive controls (QA S86)', () => {
  it('positive control: inline bare {{ token }} is flagged', () => {
    expect(flagged(component('<p>{{ token }}</p>'), `${DIR}a.component.ts`)).toBe(true);
  });

  it('positive control: [value]="signingSecret" binding is flagged', () => {
    expect(flagged(component('<input [value]="signingSecret" />'), `${DIR}b.component.ts`)).toBe(
      true,
    );
  });

  it('negative: {{ hasToken() }} presence check is not flagged', () => {
    const members = '  readonly hasToken = signal(false);';
    expect(flagged(component('<p>{{ hasToken() }}</p>', members), `${DIR}c.component.ts`)).toBe(
      false,
    );
  });

  it('negative: {{ tokenPrefix() }} display prefix is not flagged', () => {
    const members = '  readonly tokenPrefix = signal("tok_ab");';
    expect(flagged(component('<p>{{ tokenPrefix() }}</p>', members), `${DIR}d.component.ts`)).toBe(
      false,
    );
  });

  it('positive control: store field accessToken is flagged in a .store.ts file', () => {
    const code = `import { signal } from '@angular/core';
export class NamesStore {
  readonly accessToken = signal('');
}
`;
    expect(flagged(code, `${DIR}names.store.ts`)).toBe(true);
  });

  it('negative: store field passwordHash is not flagged', () => {
    const code = `import { signal } from '@angular/core';
export class NamesStore {
  readonly passwordHash = signal('');
}
`;
    expect(flagged(code, `${DIR}names2.store.ts`)).toBe(false);
  });

  it('positive control: token field in a non-reveal store is flagged', () => {
    const code = `import { signal } from '@angular/core';
export class TokenViewStore {
  readonly token = signal('');
}
`;
    expect(flagged(code, `${DIR}token-view.store.ts`)).toBe(true);
  });

  it('documented exemption: a -reveal.store.ts file is not flagged (one-time reveal, plan §8 S3)', () => {
    const code = `import { signal } from '@angular/core';
export class TokenRevealStore {
  readonly token = signal('');
}
`;
    expect(flagged(code, `${DIR}token-reveal.store.ts`)).toBe(false);
  });

  // QA defect O5 (F16, fixed in S86): the bracket-quoted secret member read used to be masked by
  // maskedQuoted() before the bare-read scan, so the rule missed it. A quoted string that is the
  // direct subscript of a member access is now unmasked and reported.
  it("regression O5: {{ store['token']() }} bracket-quoted secret read is flagged", () => {
    expect(flagged(component("<p>{{ store['token']() }}</p>"), `${DIR}e.component.ts`)).toBe(true);
    expect(flagged(component('<p>{{ row["signing_secret"] }}</p>'), `${DIR}e2.component.ts`)).toBe(
      true,
    );
  });

  // The false-positive control of the same fix: a string that is not a subscript stays masked, and
  // a metadata member read behind a subscript stays legal.
  it('negative: an array literal of strings and a metadata subscript are not flagged', () => {
    expect(flagged(component('<p [ngClass]="[\'token\']">x</p>'), `${DIR}e3.component.ts`)).toBe(
      false,
    );
    expect(flagged(component("<p>{{ row['token_prefix'] }}</p>"), `${DIR}e4.component.ts`)).toBe(
      false,
    );
  });

  // QA defect R1 (S86): a ternary branch is a read, not an object key.
  it('regression R1: a secret-shaped ternary branch is flagged', () => {
    expect(flagged(component("<p>{{ ok ? token : '' }}</p>"), `${DIR}f.component.ts`)).toBe(true);
  });

  // QA defect R5 (S86): the loop variable a `let` clause declares is not a read.
  it('regression R5: *ngFor="let token of tokens" is not flagged', () => {
    expect(
      flagged(
        component('<p *ngFor="let token of tokens">{{ token.name }}</p>'),
        `${DIR}g.component.ts`,
      ),
    ).toBe(false);
  });

  // The documented other half of R5: the DECLARATION is skipped, the BODY read of the same name is
  // not — the fix there is renaming the variable, not a rule change.
  it('regression R5: a body read of the declared loop variable is still flagged', () => {
    expect(
      flagged(component('<p *ngFor="let token of tokens">{{ token }}</p>'), `${DIR}h.component.ts`),
    ).toBe(true);
  });
});
