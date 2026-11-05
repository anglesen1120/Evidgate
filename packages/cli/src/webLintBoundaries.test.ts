// Proves the apps/web boundary rules — no HttpClient/fetch outside core, no hand-written wire
// DTO, no feature→feature import, no DomSanitizer bypass outside the reviewed artifact-viewer
// bridge, and no secret-shaped field in a Settings store — by linting inline snippets with the
// exported flat-config blocks (apps/web/lint/boundaries.cjs, including the built-in
// `no-restricted-globals` its HTTP block registers) and the typescript-eslint parser, exactly as
// eslint.config.js does. The family is split by rule: this file holds the registration proof and
// the HTTP boundary, and each sibling `webLintBoundaries.<rule>.test.ts` file holds one rule; the
// setup they share lives in `webLintBoundaries.testing.ts`.
//
// These specs live in @evidgate/cli, not in apps/web, on purpose: `ng test` compiles its
// specs with the Angular application builder for the browser, so a spec there cannot load
// ESLint and the CommonJS rule modules, which need Node's `require` (and `node:path` in the
// boundary rule). The Vitest `node` workspaces are the only test runner an existing
// scripts/verify.sh stage (`vitest`) executes in Node, and this package already hosts the
// repository-level checks over other workspaces (parity.test.ts, schemaParity.test.ts).

import { describe, expect, it } from 'vitest';

import {
  APP_CONFIG_FILE,
  BYPASS_TRUST,
  CORE_DATA_FILE,
  CORE_HTTP_FILE,
  FEATURE_BOUNDARY,
  FEATURE_FILE,
  HTTP_GLOBALS,
  HTTP_OUTSIDE_CORE,
  SECRET_FIELD,
  SHARED_FILE,
  WIRE_DTO,
  boundaries,
  lint,
  ruleIds,
} from './webLintBoundaries.testing.js';

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  it('registers the boundary rules for the application sources', () => {
    expect(boundaries.flatMap((block) => Object.keys(block.rules ?? {}))).toEqual([
      HTTP_OUTSIDE_CORE,
      HTTP_GLOBALS,
      WIRE_DTO,
      FEATURE_BOUNDARY,
      BYPASS_TRUST,
      SECRET_FIELD,
    ]);
    expect(new Set(boundaries.flatMap((block) => block.files ?? []))).toEqual(
      new Set(['apps/web/src/**/*.ts', 'apps/web/src/app/features/settings/**/*.ts']),
    );
    // The Settings-only rule is one additive block of its own, and its narrower glob is the
    // whole scoping mechanism: dropping it would report every feature's stores.
    expect(boundaries.filter((block) => block.rules?.[SECRET_FIELD] !== undefined)).toHaveLength(1);
  });

  describe('no-http-outside-core', () => {
    const httpClientImport = [
      "import { HttpClient } from '@angular/common/http';",
      '',
      'export const client = HttpClient;',
      '',
    ].join('\n');

    it('rejects an HttpClient import in a feature', () => {
      expect(ruleIds(httpClientImport, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('allows an HttpClient import in core/http and core/data', () => {
      expect(lint(httpClientImport, CORE_HTTP_FILE)).toEqual([]);
      expect(lint(httpClientImport, CORE_DATA_FILE)).toEqual([]);
    });

    it('allows only the listed @angular/common/http symbols outside core', () => {
      const allowed = [
        'import {',
        '  HttpErrorResponse,',
        '  provideHttpClient,',
        '  withInterceptors,',
        "} from '@angular/common/http';",
        '',
        'export const providers = [provideHttpClient(withInterceptors([])), HttpErrorResponse];',
        '',
      ].join('\n');
      expect(lint(allowed, APP_CONFIG_FILE)).toEqual([]);

      const banned = [
        "import { HttpBackend } from '@angular/common/http';",
        '',
        'export const backend = HttpBackend;',
        '',
      ].join('\n');
      expect(ruleIds(banned, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('rejects a re-export, a star re-export and a dynamic import of @angular/common/http', () => {
      const reExport = ["export { HttpClient } from '@angular/common/http';", ''].join('\n');
      expect(ruleIds(reExport, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const star = ["export * from '@angular/common/http';", ''].join('\n');
      expect(ruleIds(star, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const dynamic = [
        "export const load = (): Promise<unknown> => import('@angular/common/http');",
        '',
      ].join('\n');
      expect(ruleIds(dynamic, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('allows an allowed symbol through a re-export, and the whole module inside core', () => {
      const allowedReExport = [
        "export { provideHttpClient, withInterceptors } from '@angular/common/http';",
        '',
      ].join('\n');
      expect(lint(allowedReExport, APP_CONFIG_FILE)).toEqual([]);

      const typeReExport = [
        "export type { HttpErrorResponse } from '@angular/common/http';",
        '',
      ].join('\n');
      expect(lint(typeReExport, SHARED_FILE)).toEqual([]);

      const dynamic = [
        "export const load = (): Promise<unknown> => import('@angular/common/http');",
        '',
      ].join('\n');
      expect(lint(dynamic, CORE_HTTP_FILE)).toEqual([]);
      expect(lint(dynamic, CORE_DATA_FILE)).toEqual([]);
    });

    it('rejects an SDK operation, the default client and a namespace import outside core', () => {
      const operation = [
        "import { listRuns } from '@evidgate/sdk';",
        '',
        'export const load = listRuns;',
        '',
      ].join('\n');
      expect(ruleIds(operation, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const defaultClient = [
        "import { client } from '@evidgate/sdk';",
        '',
        'export const api = client;',
        '',
      ].join('\n');
      expect(ruleIds(defaultClient, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const namespace = [
        "import * as sdk from '@evidgate/sdk';",
        '',
        'export const api = sdk;',
        '',
      ].join('\n');
      expect(ruleIds(namespace, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const factories = [
        "import { createClient, createConfig } from '@evidgate/sdk';",
        '',
        'export const build = [createClient, createConfig];',
        '',
      ].join('\n');
      expect(ruleIds(factories, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE, HTTP_OUTSIDE_CORE]);

      const mixed = [
        "import { type RunPage, listRuns } from '@evidgate/sdk';",
        '',
        'export const load = (): RunPage | undefined => listRuns;',
        '',
      ].join('\n');
      expect(ruleIds(mixed, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const reExport = ["export { createConfig } from '@evidgate/sdk';", ''].join('\n');
      expect(ruleIds(reExport, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const mixedReExport = [
        "export { type RunPage, createConfig } from '@evidgate/sdk';",
        '',
      ].join('\n');
      expect(ruleIds(mixedReExport, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const star = ["export * from '@evidgate/sdk';", ''].join('\n');
      expect(ruleIds(star, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const dynamic = [
        "export const load = (): Promise<unknown> => import('@evidgate/sdk');",
        '',
      ].join('\n');
      expect(ruleIds(dynamic, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('allows SDK types and the error-envelope guard outside core', () => {
      const typeImport = [
        "import type { RunPage } from '@evidgate/sdk';",
        '',
        'export type Page = RunPage;',
        '',
      ].join('\n');
      expect(lint(typeImport, FEATURE_FILE)).toEqual([]);

      const inlineType = [
        "import { type RunPage } from '@evidgate/sdk';",
        '',
        'export type Page = RunPage;',
        '',
      ].join('\n');
      expect(lint(inlineType, FEATURE_FILE)).toEqual([]);

      const guard = [
        "import { isErrorEnvelope } from '@evidgate/sdk';",
        '',
        'export const classify = (body: unknown): boolean => isErrorEnvelope(body);',
        '',
      ].join('\n');
      expect(lint(guard, SHARED_FILE)).toEqual([]);

      const typeReExport = ["export type { RunPage } from '@evidgate/sdk';", ''].join('\n');
      expect(lint(typeReExport, SHARED_FILE)).toEqual([]);

      const inlineTypeReExport = ["export { type RunPage } from '@evidgate/sdk';", ''].join('\n');
      expect(lint(inlineTypeReExport, SHARED_FILE)).toEqual([]);
    });

    it('allows every SDK value inside core/http and core/data, specs included', () => {
      const code = [
        "import { client, createConfig, listRuns } from '@evidgate/sdk';",
        '',
        'export const all = [client, createConfig, listRuns];',
        '',
      ].join('\n');
      expect(lint(code, CORE_HTTP_FILE)).toEqual([]);
      expect(lint(code, CORE_DATA_FILE)).toEqual([]);
      expect(lint(code, 'apps/web/src/app/core/http/error-mapper.spec.ts')).toEqual([]);
      expect(lint(code, 'apps/web/src/app/core/data/runs-data.service.spec.ts')).toEqual([]);
      expect(ruleIds(code, 'apps/web/src/app/features/runs/runs.store.spec.ts')).toEqual([
        HTTP_OUTSIDE_CORE,
        HTTP_OUTSIDE_CORE,
        HTTP_OUTSIDE_CORE,
      ]);
    });

    it('rejects fetch() outside core and allows it inside core/http', () => {
      const code = [
        'export function load(url: string): Promise<Response> {',
        '  return fetch(url);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([HTTP_GLOBALS]);
      expect(lint(code, CORE_HTTP_FILE)).toEqual([]);
    });

    it('rejects window.fetch and a new WebSocket outside core', () => {
      const member = [
        'export const load = (url: string): Promise<Response> => window.fetch(url);',
        '',
      ].join('\n');
      expect(ruleIds(member, FEATURE_FILE)).toEqual([HTTP_GLOBALS]);

      const socket = [
        "export const socket = new WebSocket('wss://evidence.test/v1/stream');",
        '',
      ].join('\n');
      expect(ruleIds(socket, SHARED_FILE)).toEqual([HTTP_GLOBALS]);
    });

    it('does not flag a local binding that shadows the global', () => {
      const code = [
        'const fetch = (url: string): string => url;',
        '',
        'export const read = fetch;',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);
    });
  });
});
