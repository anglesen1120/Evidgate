import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  SECRET_FIELD,
  SETTINGS_SECRETS_FILE,
  lint,
  lintSecretAt,
  quoted,
  ruleIds,
  secretFixture,
  secretFixturePath,
} from './webLintBoundaries.testing.js';

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  describe('no-secret-field-in-store', () => {
    // Position 4: an inline template is source text, so the binding never reaches a visitor that
    // could see a property name — the template property's value is scanned by content instead.
    it('reports a secret-shaped member read in an inline template', () => {
      const templates = [
        '<p>{{ store.token() }}</p>',
        '<p>{{ row.secret }}</p>',
        '<input [value]="x.signing_secret" />',
        '<p>{{ store.plaintext }}</p>',
        '<p>{{ a.b.token }}</p>',
        '<p>{{ store?.token }}</p>',
        '<p>{{ rows[0]?.secret }}</p>',
      ];
      for (const template of templates) {
        const code = [
          '@Component({',
          "  selector: 'app-secrets',",
          `  template: \`${template}\`,`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
        const messages = lint(code, SETTINGS_SECRETS_FILE);
        expect(
          messages.map((message) => message.ruleId),
          template,
        ).toEqual([SECRET_FIELD]);
        expect(messages[0]?.message).toContain(
          'keep the value server-side or in the one-time reveal component',
        );
      }
    });

    // The copy of a secret row mentions the word; a sentence period is not a member read, and a
    // metadata binding next to it is not a bare read of a value (the bare read of a
    // secret-shaped field is now reported, see the `{{ token }}` cases below).
    it('accepts prose around the word in an inline template', () => {
      const prose = [
        'Rotate the key. Token is shown once.',
        'Copy it now. Secret values are hidden.',
        'Format, e.g. token — see the column.',
      ];
      for (const sentence of prose) {
        const code = [
          '@Component({',
          "  selector: 'app-secrets',",
          `  template: \`<p>${sentence}</p><p>{{ tokenPrefix }}</p>\`,`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), sentence).toEqual([]);
      }

      // A member read glued to its dot is still reported, and the `(` of a call, the `]` of an
      // index or the `?` of an optional chain is a member in front of the dot too.
      for (const read of [
        '{{ store.token }}',
        '{{ rows[0].secret }}',
        '{{ store.token() }}',
        '{{ store?.token }}',
      ]) {
        const code = [
          '@Component({',
          "  selector: 'app-secrets',",
          `  template: \`<p>${read}</p>\`,`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), read).toEqual([SECRET_FIELD]);
      }
    });

    it('accepts a metadata-only inline template', () => {
      const code = [
        '@Component({',
        "  selector: 'app-secrets',",
        '  template: `<p>{{ row.token_prefix }}</p>',
        '    <p>{{ store.rotationState() }} — {{ row.last4 }}</p>',
        '    <input [value]="x.secretRef" />',
        '    <p>Token rotated</p>`,',
        "  styleUrl: './x.css',",
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // A `templateUrl` file used to be a documented blind spot. The rule
    // now resolves the constant URL against the linted file's own directory and reads the file —
    // here the fixture is linted under a path INSIDE the fixture directory, so the `.html.txt`
    // next to it is the file the URL names. Reported once, on the `templateUrl` property.
    it('reports a secret-shaped member read in a templateUrl file', () => {
      const messages = lintSecretAt(
        secretFixture('failing-template-url.ts.txt'),
        secretFixturePath('failing-template-url.ts'),
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token']);
      expect(messages[0]?.message).toContain(
        'keep the value server-side or in the one-time reveal component',
      );
    });

    // The same file position with a BARE read, and prose above it that mentions the word: the
    // interpolation is reported, the sentence is not.
    it('reports a bare read in a templateUrl file', () => {
      const messages = lintSecretAt(
        secretFixture('failing-template-url-bare.ts.txt'),
        secretFixturePath('failing-template-url-bare.ts'),
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token']);
    });

    // A binding VALUE in a `templateUrl` file, in a template that holds nothing else, so the
    // `[value]` read is the one whose name the message carries.
    it('reports a bare binding value in a templateUrl file', () => {
      const messages = lintSecretAt(
        secretFixture('failing-template-url-binding.ts.txt'),
        secretFixturePath('failing-template-url-binding.ts'),
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['signingSecret']);
    });

    // A bare `Linter` hands the rule the filename it was given: an absolute one under the CLI, and
    // a relative one here. A relative filename is resolved against `process.cwd()`, so the same
    // fixture reports the same finding either way.
    it('resolves a templateUrl from an absolute and from a relative filename', () => {
      const code = secretFixture('failing-template-url.ts.txt');
      const absolute = secretFixturePath('failing-template-url.ts');
      expect(lintSecretAt(code, absolute).map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(
        lintSecretAt(code, relative(process.cwd(), absolute)).map((message) => message.ruleId),
      ).toEqual([SECRET_FIELD]);
    });

    // A metadata-only file, a file that does not exist, a URL with a scheme, an absolute path and
    // a URL that is not a constant string: the rule reports nothing and never throws.
    it('accepts a templateUrl that is missing, remote, absolute or not constant', () => {
      expect(
        lintSecretAt(
          secretFixture('passing-template-url.ts.txt'),
          secretFixturePath('passing-template-url.ts'),
        ),
      ).toEqual([]);
    });

    // The read is bounded: a file over `MAX_TEMPLATE_CHARS` is not read at all, so a violation in
    // it is not reported and a huge file cannot slow the lint run down. The file just under the
    // bound reports, which is what proves the size is what skipped the oversized one.
    it('skips a templateUrl file over the read bound', () => {
      const directory = mkdtempSync(join(tmpdir(), 'evidgate-template-url-'));
      const component = (templateUrl: string): string =>
        [
          '@Component({',
          "  selector: 'app-secrets',",
          `  templateUrl: '${templateUrl}',`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
      try {
        writeFileSync(
          join(directory, 'oversized.html'),
          `{{ token }}${'x'.repeat(300_000)}`,
          'utf8',
        );
        const oversizedFile = join(directory, 'oversized.ts');
        writeFileSync(oversizedFile, component('./oversized.html'), 'utf8');
        expect(lintSecretAt(readFileSync(oversizedFile, 'utf8'), oversizedFile)).toEqual([]);

        writeFileSync(join(directory, 'small.html'), '{{ token }}', 'utf8');
        const smallFile = join(directory, 'small.ts');
        writeFileSync(smallFile, component('./small.html'), 'utf8');
        expect(
          lintSecretAt(readFileSync(smallFile, 'utf8'), smallFile).map((message) => message.ruleId),
        ).toEqual([SECRET_FIELD]);
      } finally {
        rmSync(directory, { recursive: true, force: true });
      }
    });

    // Position 6 in an inline template: a bare interpolation next to a bare binding value, and
    // the binding value on its own.
    it('reports a bare interpolation and a bare binding value in an inline template', () => {
      const code = [
        '@Component({',
        "  selector: 'app-secrets',",
        `  template: '<p>{{ token }}</p><input [value]="signingSecret" />',`,
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      const messages = lint(code, SETTINGS_SECRETS_FILE);
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token']);

      const bindingOnly = [
        '@Component({',
        "  selector: 'app-secrets',",
        `  template: '<input [value]="signingSecret" />',`,
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      expect(quoted('Do not bind ', lint(bindingOnly, SETTINGS_SECRETS_FILE))).toEqual([
        'signingSecret',
      ]);
    });

    // Every expression kind Angular evaluates, one component per kind: an interpolation, a
    // property binding on its own, an `@if` condition, the right-hand side of an `@let`, the
    // iterated expression of an `@for`, a structural directive and an `@switch` subject.
    it('reports a bare read in a block header and a structural directive', () => {
      const messages = lint(secretFixture('failing-template-bare.ts.txt'), SETTINGS_SECRETS_FILE);
      expect(messages.map((message) => message.ruleId)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
      expect(quoted('Do not bind ', messages)).toEqual([
        'token',
        'signingSecret',
        'password',
        'signingSecret',
        'pendingToken',
        'secretValue',
        'credential',
      ]);
    });

    // QA defect R1 (S86): a ternary BRANCH is a read. The identifier in front of a `:` used to be
    // skipped as an object key, so `{{ ok ? token : '' }}` and `[title]="a ? secret : b"` were
    // never reported.
    it('reports a bare read in a ternary branch', () => {
      const messages = lint(
        secretFixture('failing-template-ternary.ts.txt'),
        SETTINGS_SECRETS_FILE,
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD, SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token', 'secret']);

      // A `,` in front of the name is not an object member either when a `?` is still awaiting its
      // `:`: the branch is a read, so a call inside it cannot hide one behind its argument list.
      const commaInCall = [
        '@Component({',
        "  selector: 'app-secrets',",
        `  template: \`<p>{{ ok ? pick(a, token) : '' }}</p>\`,`,
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      expect(quoted('Do not bind ', lint(commaInCall, SETTINGS_SECRETS_FILE))).toEqual(['token']);
    });

    // The key position is unchanged: `{ token: active }` declares a class name, and an array
    // literal of strings is not a subscript. A `@defer` block with no header evaluates nothing.
    it('accepts a secret-shaped name as an object key', () => {
      expect(lint(secretFixture('passing-template-keys.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual(
        [],
      );
    });

    // QA defects R2 and R4 (S86): `@case (...)` and `@defer (when ...)` are headers Angular
    // evaluates, and an `@for` whose `of` is wrapped onto the next line still names its iterated
    // expression.
    it('reports a bare read in an @case, an @defer and a wrapped @for header', () => {
      const messages = lint(
        secretFixture('failing-template-block-headers.ts.txt'),
        SETTINGS_SECRETS_FILE,
      );
      expect(messages.map((message) => message.ruleId)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
      expect(quoted('Do not bind ', messages)).toEqual(['credential', 'password', 'pendingToken']);
    });

    // QA defect R5 (S86): the identifier a `let` clause declares in a `*` structural directive is a
    // declaration, not a read, and so is the alias of an `index as name` clause.
    it('accepts a structural directive let declaration and its index alias', () => {
      expect(
        lint(secretFixture('passing-template-loop-let.ts.txt'), SETTINGS_SECRETS_FILE),
      ).toEqual([]);
    });

    // QA defect O5 (S86): a quoted string that is the DIRECT subscript of a member access names the
    // member, so `store['token']()` and `row["signing_secret"]` are reads. Every other string —
    // prose, an array literal, a template reference — stays masked.
    it('reports a bracket-quoted secret member read', () => {
      const messages = lint(
        secretFixture('failing-template-subscript.ts.txt'),
        SETTINGS_SECRETS_FILE,
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD, SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token', 'signing_secret']);
    });

    // A member read of a metadata name, an `@for` alias, a template ref, a quoted string, an
    // object key, an `@let` declaration, a presence name and HTML text stay legal.
    it('accepts metadata, aliased and quoted template bindings', () => {
      expect(
        lintSecretAt(
          secretFixture('passing-templates.ts.txt'),
          secretFixturePath('passing-templates.ts'),
        ),
      ).toEqual([]);
    });

    // Position 7: a one-time result type in state, at every reported position — a `…State` member,
    // a plain annotated field, an accessor, a `signal`/`computed` type argument, a constructor
    // parameter property, a `…Store` member and a `withState` type argument.
    it('reports a secret-carrying result type held in state', () => {
      const messages = lint(secretFixture('failing-result-type.ts.txt'), SETTINGS_SECRETS_FILE);
      expect(messages.map((message) => message.ruleId)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
      expect(quoted('Do not type Settings state with ', messages)).toEqual([
        'IssuedInvite',
        'InviteIssuedView',
        'IssuedInvite',
        'RotationResult',
        'RotationResult',
        'IngestTokenCreatedResponse',
        'CiBindingCreatedResponse',
        'CiBindingCreatedResponse',
      ]);
      for (const message of messages) {
        expect(message.message).toContain('may only be a RETURN value, never state');
      }
    });

    // The sanctioned shape: a method that RETURNS one, a parameter, a local variable, the
    // declaration of the type itself, and a component class (which is not a store).
    it('accepts a secret-carrying result type in a return value, a parameter and a local', () => {
      expect(lint(secretFixture('passing-result-type.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // The two reviewed exemptions hold for the new positions as well: a spec has to spell the
    // name, and the reveal component is the one place allowed to hold a value.
    it('exempts a spec file and the reveal component from the new positions', () => {
      for (const fixture of [
        'failing-template-bare.ts.txt',
        'failing-result-type.ts.txt',
        'failing-template-url.ts.txt',
      ]) {
        expect(
          ruleIds(
            secretFixture(fixture),
            'apps/web/src/app/features/settings/secrets/secrets.store.spec.ts',
          ),
          fixture,
        ).toEqual([]);
        expect(
          ruleIds(
            secretFixture(fixture),
            'apps/web/src/app/features/settings/invites/reveal/invite-reveal.component.ts',
          ),
          fixture,
        ).toEqual([]);
      }
    });
  });
});
