import { describe, expect, it } from 'vitest';

import {
  SECRET_FIELD,
  SETTINGS_SECRETS_FILE,
  lint,
  ruleIds,
  secretFixture,
} from './webLintBoundaries.testing.js';

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  // A Settings store holds metadata only — `token_prefix`, `rotation_state`, `last_rotated_at` —
  // and the value is revealed exactly once by the shared reveal dialog, never stored, re-fetched
  // or bound.
  describe('no-secret-field-in-store', () => {
    const failing = secretFixture('failing.ts.txt');
    const passing = secretFixture('passing.ts.txt');

    it('rejects a secret-shaped field in a state interface, a state literal and a store class', () => {
      // One finding per reported position: the interface member, the state literal key, the field,
      // the private field and the constructor parameter property, in source order.
      const expected = [SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD];
      expect(ruleIds(failing, SETTINGS_SECRETS_FILE)).toEqual(expected);
      for (const message of lint(failing, SETTINGS_SECRETS_FILE)) {
        expect(message.message).toContain(
          'a Settings store holds metadata only (plan 09.7.1 §8 S3): keep the value server-side or in the one-time reveal component',
        );
      }
    });

    it('accepts the metadata-only names of the same positions', () => {
      expect(lint(passing, SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // The scope is the block's `files` glob, not the rule: a store outside Settings keeps its
    // own naming, and the rule must not creep into other features.
    it('leaves a store outside the Settings feature alone', () => {
      expect(ruleIds(failing, 'apps/web/src/app/features/runs/runs.store.ts')).toEqual([]);
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings-adjacent/secrets.store.ts'),
      ).toEqual([]);
    });

    // A spec has to spell the forbidden name to assert its absence, and the one-time reveal
    // component is the single reviewed place allowed to hold a value in memory.
    it('exempts a spec file and the one-time reveal component', () => {
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/secrets.store.spec.ts'),
      ).toEqual([]);
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/reveal/reveal-dialog.ts'),
      ).toEqual([]);
      expect(
        ruleIds(
          failing,
          'apps/web/src/app/features/settings/secrets/signing-secret-reveal.component.ts',
        ),
      ).toEqual([]);
      // `reveal` must be a whole word of the basename: a longer word that merely contains it
      // (`revealed`, `unrevealed`, `secrets-revealed-store`) cannot switch the rule off.
      const allFive = [SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD];
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/secrets-revealed-store.ts'),
      ).toEqual(allFive);
      expect(ruleIds(failing, 'apps/web/src/app/features/settings/secrets/unrevealed.ts')).toEqual(
        allFive,
      );
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/revealed.store.ts'),
      ).toEqual(allFive);
      // Only an exact directory segment or a `reveal` word of the basename exempts: a look-alike
      // directory and a `..` that leaves the reveal one stay reportable.
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/not-reveal/secrets.store.ts'),
      ).toEqual(allFive);
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/reveal/../secrets.store.ts'),
      ).toEqual(allFive);
    });

    it('reports every secret-shaped row of the name table', () => {
      const objectKeys = [
        'token',
        'secret',
        'password',
        'passwd',
        'plaintext',
        'credential',
        'apikey',
        'API_KEY',
        'apiKey',
        'signing_secret',
        'signingSecret',
        'secretValue',
        'private_key',
        'privateKey',
        'accessToken',
        'refreshToken',
        'bearerToken',
        'clientSecret',
        'tokenValue',
        'secret_text',
        'passwordRaw',
        // A secret word in the trailing position names the value, whatever the leading words are.
        'ingestToken',
        'apiToken',
        'webhookSecret',
        'rawToken',
        'plainSecret',
        'secretKey',
      ];
      for (const name of objectKeys) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }

      // The same table in the declaration position, where camelCase keeps the wire-DTO rule
      // quiet: `interface SecretsState` is what makes its members store state.
      const signatures = [
        'token',
        'secret',
        'password',
        'passwd',
        'plaintext',
        'credential',
        'apikey',
        'apiKey',
        'signingSecret',
        'secretValue',
        'privateKey',
        'accessToken',
        'refreshToken',
        'bearerToken',
        'clientSecret',
        'tokenValue',
        'secretRaw',
        'passwordText',
      ];
      for (const name of signatures) {
        const code = [
          'export interface SecretsState {',
          `  readonly ${name}: string;`,
          '}',
          '',
        ].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }
    });

    it('accepts every metadata row of the name table', () => {
      const signatures = [
        'tokenPrefix',
        'tokenName',
        'last4',
        'rotationState',
        'secretRef',
        'secretName',
        'ingestTokens',
        'tokenCount',
        'expiresAt',
        'hasSecret',
        'hasToken',
        'tokenExpiresAt',
        'lastRotatedAt',
        'ciBindings',
        'passwordHash',
        'tokens',
      ];
      for (const name of signatures) {
        const code = [
          'export interface SecretsState {',
          `  readonly ${name}: string;`,
          '}',
          '',
        ].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), name).toEqual([]);
      }

      const objectKeys = ['token_prefix', 'secret_reference', 'last_rotated_at', 'ingest_tokens'];
      for (const name of objectKeys) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), name).toEqual([]);
      }
    });

    // No import: the rule matches the callee name, and the workspace has no `@ngrx/signals`
    // dependency (the app's own store seam is shaped like a signalStore without the package).
    it('reports a state literal of withState, patchState and signalStore at any depth', () => {
      const code = [
        'export const Store = signalStore(withState({ meta: { plaintext: "" } }));',
        '',
        'export function save(store: unknown): void {',
        '  patchState(store, { token: "" });',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD, SECRET_FIELD]);
    });

    // A request body is what the API receives, not what the store holds, and a nested `withState`
    // is the literal its own call reports: the walk stops at both boundaries.
    it('walks only the state literal, not a payload inside a closure or a nested withState', () => {
      const code = [
        'export const Store = signalStore(',
        '  withState({ tokenPrefix: "" }),',
        '  withMethods((store) => {',
        '    return {',
        '      save(token: string): void {',
        "        post('/v1/settings/secrets', { token });",
        '      },',
        '    };',
        '  }),',
        '  withState({ secretRef: "" }),',
        ');',
        '',
      ].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE)).toEqual([]);

      // The nested literal is still reported once, from its own `withState` call.
      const nested = [
        'export const Store = signalStore(withState({ meta: { token: "" } }));',
        '',
      ].join('\n');
      expect(ruleIds(nested, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD]);
    });

    it('reports a signalState literal', () => {
      const code = ['export const state = signalState({ password: "" });', ''].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD]);
    });

    // A state factory or
    // updater hands the literal it RETURNS over as state, so the argument is walked too — a
    // concise body, a block body's top-level `return`, and a `signal({ ... })` inside the factory.
    // One finding per occurrence: the factory literal, the nested literal, the wrapped one and the
    // three middle-of-name keys are all reported once, and nothing twice.
    it('reports a secret-shaped field of a state factory or updater', () => {
      const extended = secretFixture('failing-extended.ts.txt');
      const expected = [
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ];
      expect(ruleIds(extended, SETTINGS_SECRETS_FILE)).toEqual(expected);
      for (const message of lint(extended, SETTINGS_SECRETS_FILE)) {
        expect(message.message).toContain(
          'a Settings store holds metadata only (plan 09.7.1 §8 S3)',
        );
      }
    });

    // The same extension must not turn a metadata factory, a neutral word or a payload into a
    // finding: `withState(() => ({...}))` writes the ordinary metadata names, a method's request
    // body and a nested arrow's object are still not state, and `tokenizer`, `secretary`,
    // `passwordless`/`passwordLess` and the pagination cursor fields stay legal.
    it('accepts a factory or updater that hands over metadata only', () => {
      expect(lint(secretFixture('passing-extended.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // A class a store delegates to (`MembersInviteFlow`, `SecretsRotationTrack`) holds state the
    // same way, so the store-class selector covers the `Flow` and `Track` suffixes and nothing
    // that merely resembles them (`Workflow`).
    it('reports a secret-shaped field of a Flow or Track class split out of a store', () => {
      expect(ruleIds(secretFixture('failing-split-classes.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual(
        [SECRET_FIELD, SECRET_FIELD, SECRET_FIELD],
      );
      expect(lint(secretFixture('passing-split-classes.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual(
        [],
      );
    });

    // A secret word behind a value word is the value itself, wherever it stands in the name:
    // position is not what decides it, the word behind it is. A secret word followed by anything
    // else is metadata (`rawTokenMaterial`, `nextTokenRefreshAt` — see the README blind spots).
    it('reports a secret word in the middle of a name', () => {
      const reported = [
        'rawTokenValue',
        'signingSecretText',
        'passwordRawText',
        'pageTokenText',
        // The trailing position was already reported before T2 and still is: `pageToken` is not a
        // neutral name.
        'pageToken',
      ];
      for (const name of reported) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }

      const neutral = [
        'tokenizer',
        'secretary',
        'passwordless',
        'passwordLess',
        'hasTokenValue',
        'rawTokenMaterial',
        'nextTokenRefreshAt',
        'cursor',
        'next_cursor',
      ];
      for (const name of neutral) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), name).toEqual([]);
      }
    });

    // The split is bounded. A name longer than 128 characters is examined in its first
    // and last 64, so the work per identifier stays constant; a secret word outside both windows
    // is not seen, which the README lists as a blind spot rather than implying it is covered.
    it('bounds the name it splits', () => {
      const head = `tokenValue_${'a'.repeat(140)}`;
      const tail = `${'a'.repeat(140)}_tokenValue`;
      const buried = `${'a'.repeat(70)}token${'b'.repeat(70)}`;
      expect(head.length).toBeGreaterThan(128);
      expect(tail.length).toBeGreaterThan(128);
      expect(buried.length).toBeGreaterThan(128);

      for (const name of [head, tail]) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }

      const code = [`export const state = withState({ ${buried}: '' });`, ''].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE), buried).toEqual([]);
    });

    it('reports a withState type argument literal', () => {
      const code = [
        'export const feature = withState<{ token: string; tokenPrefix: string }>({',
        "  tokenPrefix: '',",
        '});',
        '',
      ].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD]);
    });

    // Position 3: the members of a `/Store$/` class, getters included. The top-level signal and
    // the non-store class are here so the narrow scope is pinned, not implied.
    it('reports a store class field, accessor and getter, and nothing else', () => {
      const code = [
        "import { signal } from '@angular/core';",
        '',
        'export class SecretsStore {',
        "  readonly token = signal('');",
        "  accessor password = '';",
        '  get signingSecret(): string {',
        "    return '';",
        '  }',
        '}',
        '',
        "export const token = signal('');",
        '',
        'export class SecretsPanel {',
        "  readonly token = '';",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
    });

    // A private field and a constructor parameter property are fields of the instance too, and
    // they are where a value is easiest to hide from review.
    it('reports a private field and a constructor parameter property of a store class', () => {
      const code = [
        'export class SecretsStore {',
        "  #token = '';",
        '',
        '  #helper = 0;',
        '',
        '  constructor(private readonly password: string, scope: string) {',
        '    this.#helper = scope.length;',
        '  }',
        '}',
        '',
        'export class SecretsPanel {',
        "  #token = '';",
        '',
        '  constructor(private readonly password: string) {}',
        '}',
        '',
      ].join('\n');
      // The private field, then the parameter property: a plain `scope` parameter and a private
      // field of a non-store class are not fields a Settings template binds as state.
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD, SECRET_FIELD]);
    });

    it('does not report a declaration that is neither a state nor a store', () => {
      const code = [
        'export interface SecretRow {',
        '  token: string;',
        '}',
        '',
        'export type SecretList = { password: string };',
        '',
      ].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE)).toEqual([]);
    });
  });
});
