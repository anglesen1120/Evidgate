/*
 * Keeps every body-carrying request of the CLI under a local cap. The scan itself lives in
 * requestCapScan.testing.ts (what it follows, and the limits syntax cannot cross); this file
 * runs it over the production sources and proves it sees each bypass form with fixtures.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { indirectForms } from './requestCapScan.fixtures.testing.js';
import { requestBodyCaps } from './requestCaps.js';
import {
  enclosingFunction,
  type Finding,
  operationRoutes,
  scanSource,
} from './requestCapScan.testing.js';

const sourceRoot = import.meta.dirname;
const sdkText = readFileSync(join(sourceRoot, '../../sdk/gen/sdk.gen.ts'), 'utf8');

/**
 * `file#function` of every direct send that may carry a body an SDK cap cannot bound, with why
 * it is bounded. A row exempts only the findings the send path reports in that function — direct
 * fetch, `Request` construction, unresolved fetch and other transport — because it names the
 * bound that holds for that send. SDK-route findings (`uncapped-route`, `unprovable-args`,
 * `unprovable-spread`, `unresolved-url`) are not exempted: they name a route, not a send.
 */
const directFetchAllowList: Readonly<Record<string, string>> = {
  'uploadPut.ts#sendPutRequest':
    'presigned artifact PUT: signed Content-Length equals the fstat size and the streamed body aborts past it (BodyStream); only four kinds have a static cap',
  'client.ts#createApiClient':
    'wraps the SDK request that the request interceptor already measured',
  'commands/gate.ts#refuseRedirects':
    'passes the SDK request through unchanged after the request interceptor measured its body; only a refused redirect is rewritten',
};

function productionFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return productionFiles(path);
    return /\.ts$/.test(entry.name) && !/\.(test|testing)\.ts$/.test(entry.name) ? [path] : [];
  });
}

const routes = operationRoutes(sdkText);

describe('request body cap scan', () => {
  it('reads the generated operations (sanity: the route map is not empty)', () => {
    expect(routes.get('createRun')).toBe('POST /projects/{p}/runs');
    expect(routes.get('postRunEvents')).toBe('POST /runs/{r}/events');
  });

  it('finds no request body without a cap in the production sources', () => {
    const findings: Finding[] = [];
    const used = new Set<string>();
    for (const path of productionFiles(sourceRoot)) {
      const result = scanSource(
        relative(sourceRoot, path),
        readFileSync(path, 'utf8'),
        routes,
        requestBodyCaps,
        directFetchAllowList,
      );
      findings.push(...result.findings);
      for (const route of result.usedRoutes) used.add(route);
    }
    expect(findings).toEqual([]);
    // Every table row is reached by a send site, so a stale row cannot hide a removed cap.
    expect([...used].sort()).toEqual(Object.keys(requestBodyCaps).sort());
  });

  it('names a stale allow-list entry', () => {
    const present = new Set<string>();
    for (const path of productionFiles(sourceRoot)) {
      const file = relative(sourceRoot, path);
      const sf = ts.createSourceFile(
        file,
        readFileSync(path, 'utf8'),
        ts.ScriptTarget.Latest,
        true,
      );
      const walk = (node: ts.Node): void => {
        if (ts.isCallExpression(node)) present.add(`${file}#${enclosingFunction(node)}`);
        ts.forEachChild(node, walk);
      };
      walk(sf);
    }
    for (const key of Object.keys(directFetchAllowList)) expect(present.has(key), key).toBe(true);
  });

  describe('fixtures prove the scan sees a bypass', () => {
    const fixtureRoutes = new Map([
      ['postWidgets', 'POST /widgets'],
      ['createRun', 'POST /projects/{p}/runs'],
    ]);
    const scan = (text: string): Finding[] =>
      scanSource('fixture.ts', text, fixtureRoutes, requestBodyCaps, {}).findings;

    it('a new SDK body route with no table row is red', () => {
      const found = scan(
        'export function sendWidget(client: C) { return postWidgets({ client, body: { a: 1 } }); }',
      );
      expect(found).toEqual([
        {
          file: 'fixture.ts',
          function: 'sendWidget',
          kind: 'uncapped-route',
          detail: 'POST /widgets',
        },
      ]);
    });

    it('a direct client.post with a body on an unlisted url is red', () => {
      const found = scan(
        "const p = '/widgets'; export function raw(client: C) { return client.post({ url: p, body: 'x' }); }",
      );
      expect(found.map((f) => f.kind)).toEqual(['uncapped-route']);
    });

    it('a direct fetch with a body outside the allow-list is red', () => {
      const found = scan(
        "export async function leak(u: string) { await fetch(u, { method: 'POST', body: 'x' }); }",
      );
      expect(found.map((f) => f.kind)).toEqual(['uncapped-fetch']);
    });

    it.each([
      ["postWidgets({ 'body': 'x' })", 'uncapped-route'],
      ["postWidgets({ ['body']: 'x' })", 'uncapped-route'],
      ["fetch(u, { 'body': 'x' })", 'uncapped-fetch'],
      ["fetch(u, { ['body']: 'x' })", 'uncapped-fetch'],
      ["const send = fetch; send(u, { method: 'POST', body: 'x' })", 'uncapped-fetch'],
      ["const send = deps.fetch; send(u, { body: 'x' })", 'uncapped-fetch'],
      ["fetchImpl(u, { method: 'POST', body: 'x' })", 'uncapped-fetch'],
      ["o.fetch(u, { body: 'x' })", 'uncapped-fetch'],
      ["fetch(new Request(u, { method: 'POST', body: 'x' }))", 'uncapped-fetch'],
      ["fetch(new Request(u, { ['body']: 'x' }))", 'uncapped-fetch'],
      ['fetch(new Request(u, { ...init, redirect }))', 'uncapped-fetch'],
      ['fetch(new Request(u, init))', 'uncapped-fetch'],
      ['fetch(new Request(u, makeInit()))', 'uncapped-fetch'],
      ['fetch(new Request(u))', 'uncapped-fetch'],
      ['fetch(request)', 'uncapped-fetch'],
    ])('detects the body-carrying form %s', (statement, kind) => {
      expect(
        scan(`function leak(u: string, request: Request) { ${statement}; }`).map(
          (finding) => finding.kind,
        ),
      ).toEqual([kind]);
    });

    // Each row: a form that carried a body past the scan before, its body-free twin, and the
    // words the failure text must contain so the report names the form.
    it.each(indirectForms)(
      'flags %s and keeps its body-free twin clean',
      (_name, bad, twin, kind, words) => {
        const wrap = (statement: string): string =>
          /^(import|export)\b/.test(statement) || statement.startsWith('//')
            ? statement
            : `async function leak(
                 u: string,
                 deps: Deps,
                 ok: boolean,
                 args: unknown[],
                 request: Request,
                 client: C,
                 config: unknown,
                 dynamicUrl: string,
                 page: unknown,
               ): Promise<void> { ${statement}; }`;
        const found = scan(wrap(bad));
        expect(found.map((finding) => finding.kind)).toContain(kind);
        expect(found.map((finding) => finding.detail).join('\n')).toContain(words);
        expect(scan(wrap(twin))).toEqual([]);
      },
    );

    it('does not let a flagged form hide behind a clean first send', () => {
      const found = scan(
        "function leak(u: string) { fetch(u); const f = fetch; f(u, { body: 'x' }); }",
      );
      expect(found.map((finding) => finding.kind)).toEqual(['uncapped-fetch']);
    });

    it('an allow-listed function may send through the forms, others may not', () => {
      const text =
        "function sendPutRequest(u: string) { fetch.call(null, u, { body: 'x' }); } function other(u: string) { fetch.call(null, u, { body: 'x' }); }";
      const found = scanSource('f.ts', text, fixtureRoutes, requestBodyCaps, {
        'f.ts#sendPutRequest': 'bounded',
      }).findings;
      expect(found.map((finding) => finding.function)).toEqual(['other']);
    });

    it('normalizes quoted and computed client route keys', () => {
      expect(
        scan("client.post({ ['url']: '/widgets', 'body': 'x' })").map((finding) => finding.kind),
      ).toEqual(['uncapped-route']);
    });

    it.each([
      'fetch(u)',
      "fetch('/widgets')",
      'fetch(new Request(u, { method: "GET" }))',
      'fetch(new Request(u, ({ method: "GET" })))',
      'const request = new Request(u, { method: "GET" }); fetch(request)',
      'fetchPage(cursor)',
      'fetchActionsIdToken(config, fetchImpl, deps)',
      'Reflect.apply(page, null, [u])',
    ])('keeps the provably body-free form %s clean', (statement) => {
      expect(
        scan(
          `function get(u: string, config: unknown, deps: Deps, page: unknown) { ${statement}; }`,
        ),
      ).toEqual([]);
    });

    it('a body hidden in a spread is reported as unprovable', () => {
      const found = scan(
        'export function sneaky(client: C, extra: object) { return createRun({ client, ...extra }); }',
      );
      expect(found.map((f) => f.kind)).toEqual(['unprovable-spread']);
    });

    it('a capped route and a GET fetch are clean', () => {
      expect(
        scan('export function ok(client: C) { return createRun({ client, body: {} }); }'),
      ).toEqual([]);
      expect(
        scan('export async function get(u: string) { await fetch(u, { method: "GET" }); }'),
      ).toEqual([]);
    });
  });
});
