import { describe, expect, it } from 'vitest';

import {
  CORE_DATA_FILE,
  FEATURE_FILE,
  WIRE_DTO,
  lint,
  ruleIds,
} from './webLintBoundaries.testing.js';

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  describe('no-hand-written-wire-dto', () => {
    it('rejects an interface with a snake_case wire key', () => {
      const code = ['export interface RunRow {', '  run_id: string;', '}', ''].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([WIRE_DTO]);
    });

    it('rejects a payload-shaped type alias', () => {
      const code = ['export type RunDto = {', '  id: string;', '};', ''].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([WIRE_DTO]);
    });

    it('checks core as well: no wire type anywhere in the app', () => {
      const code = [
        'export interface RunCursor {',
        "  'next_cursor': string | null;",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, CORE_DATA_FILE)).toEqual([WIRE_DTO]);
    });

    it('accepts a camelCase view model', () => {
      const viewModel = ['export interface RunRowViewModel {', '  runId: string;', '}', ''].join(
        '\n',
      );
      expect(lint(viewModel, FEATURE_FILE)).toEqual([]);

      const alias = ['export type RunRowView = {', '  runId: string;', '};', ''].join('\n');
      expect(lint(alias, FEATURE_FILE)).toEqual([]);
    });

    it('does not treat an underscore-prefixed private or phantom key as a wire key', () => {
      const code = [
        'export interface RunRowViewModel {',
        '  _private: string;',
        '  __brand: never;',
        '  runId: string;',
        '}',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);
    });

    it('accepts an inline error-envelope value in a spec', () => {
      const code = [
        "import { isErrorEnvelope } from '@evidgate/sdk';",
        '',
        "export const body = { error: { code: 'forbidden', request_id: 'r-1' } };",
        '',
        'export const parsed = isErrorEnvelope(body);',
        '',
      ].join('\n');
      expect(lint(code, 'apps/web/src/app/core/http/error-mapper.spec.ts')).toEqual([]);
    });
  });
});
