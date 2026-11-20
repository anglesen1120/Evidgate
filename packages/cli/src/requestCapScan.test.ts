/*
 * Finds every site that sends a request body, by syntax, and fails when one has no cap.
 * Two kinds of site exist: a generated SDK operation (or the client's own `post`) called with
 * a `body`, whose `METHOD route` must be a row of the cap table; and a direct `fetch` with a
 * `body`, which bypasses the client and is allowed only in the functions listed below.
 * The scan resolves the property forms a body can take (unquoted, quoted `'body'` and
 * computed `['body']` keys), a fetch-named callee (`fetch`, `fetchImpl`, `deps.fetch`) and a
 * one-hop alias of one (`const send = fetch`). A `fetch` whose first argument is a
 * `new Request(input, init)` expression carries a body unless `init` is an object literal
 * without `body` and without a spread; a `Request`-typed argument with no provable init
 * carries one too, so a wrapper cannot hide a body behind an identifier.
 * Syntax cannot follow a body handed to a helper in another module, a computed `url`, or an
 * options object built elsewhere; a spread in the options is reported as unprovable so a
 * `body` cannot hide in it.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { requestBodyCaps } from './requestCaps.js';

const sourceRoot = import.meta.dirname;
const sdkText = readFileSync(join(sourceRoot, '../../sdk/gen/sdk.gen.ts'), 'utf8');

/** `file#function` of every direct fetch that may carry a body, with why it is bounded. */
const directFetchAllowList: Readonly<Record<string, string>> = {
  'uploadPut.ts#sendPutRequest':
    'presigned artifact PUT: signed Content-Length equals the fstat size and the streamed body aborts past it (BodyStream); only four kinds have a static cap',
  'client.ts#createApiClient':
    'wraps the SDK request that the request interceptor already measured',
  'commands/gate.ts#refuseRedirects':
    'passes the SDK request through unchanged after the request interceptor measured its body; only a refused redirect is rewritten',
};

type Finding = { file: string; function: string; kind: string; detail: string };
const verbs = new Set(['post', 'put', 'patch', 'delete']);

/** `operationName -> "METHOD /path/{param}"` read from the generated operations file. */
function operationRoutes(text: string): Map<string, string> {
  const routes = new Map<string, string>();
  for (const chunk of text.split('export const ').slice(1)) {
    const match = /^(\w+) = [\s\S]*?\)\.(post|put|patch|delete)<[\s\S]*?\burl: '([^']+)'/.exec(
      chunk,
    );
    if (match !== null) {
      routes.set(match[1]!, `${match[2]!.toUpperCase()} ${match[3]!}`);
    }
  }
  return routes;
}

function enclosingFunction(node: ts.Node): string {
  for (let current: ts.Node | undefined = node.parent; current; current = current.parent) {
    if (ts.isFunctionDeclaration(current) || ts.isMethodDeclaration(current)) {
      if (current.name !== undefined) return current.name.getText();
    }
    if (ts.isVariableDeclaration(current) && current.initializer !== undefined) {
      const init = current.initializer;
      if (ts.isArrowFunction(init) || ts.isFunctionExpression(init)) return current.name.getText();
    }
  }
  return '<module>';
}

/** The static name of a property, including `'body'` and computed `['body']` keys. */
function propertyName(name: ts.PropertyName): string | undefined {
  if (ts.isComputedPropertyName(name)) {
    return ts.isStringLiteralLike(name.expression) ? name.expression.text : undefined;
  }
  if (ts.isIdentifier(name) || ts.isStringLiteralLike(name) || ts.isNumericLiteral(name)) {
    return name.text;
  }
  return undefined;
}

function propertyNamed(
  arg: ts.ObjectLiteralExpression,
  name: string,
): ts.ObjectLiteralElementLike | undefined {
  return arg.properties.find((p) => p.name !== undefined && propertyName(p.name) === name);
}

/** Whether a reference names a fetch: `fetch`, `fetchImpl`, `o.fetch`, `deps.fetch`, ... */
function fetchLikeName(text: string): boolean {
  return /fetch/i.test(text);
}

/** `name -> variable` for `const send = fetch` / `const send = deps.fetch` (one hop only). */
function fetchAliases(sf: ts.SourceFile): Set<string> {
  const aliases = new Set<string>();
  const walk = (node: ts.Node): void => {
    if (
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.initializer !== undefined
    ) {
      const init = node.initializer;
      const reference = ts.isIdentifier(init)
        ? init.text
        : ts.isPropertyAccessExpression(init)
          ? init.name.text
          : undefined;
      if (reference !== undefined && fetchLikeName(reference)) aliases.add(node.name.text);
    }
    ts.forEachChild(node, walk);
  };
  walk(sf);
  return aliases;
}

function isFetchCallee(callee: ts.Expression, aliases: ReadonlySet<string>): boolean {
  if (ts.isIdentifier(callee)) return fetchLikeName(callee.text) || aliases.has(callee.text);
  return ts.isPropertyAccessExpression(callee) && fetchLikeName(callee.name.text);
}

/** Identifiers declared `Request` (a parameter or a typed variable) with no visible init. */
function requestTypedNames(sf: ts.SourceFile): Set<string> {
  const names = new Set<string>();
  const walk = (node: ts.Node): void => {
    if (
      (ts.isParameter(node) || ts.isVariableDeclaration(node)) &&
      node.type !== undefined &&
      ts.isIdentifier(node.name) &&
      /(^|\.)Request$/.test(node.type.getText())
    ) {
      names.add(node.name.text);
    }
    ts.forEachChild(node, walk);
  };
  walk(sf);
  return names;
}

/** Whether a single fetch argument proves it carries a body (a `Request` or a typed one). */
function requestCarriesBody(
  arg: ts.Expression | undefined,
  requestTyped: ReadonlySet<string>,
): boolean {
  if (arg === undefined) return false;
  if (
    ts.isNewExpression(arg) &&
    ts.isIdentifier(arg.expression) &&
    arg.expression.text === 'Request'
  ) {
    const init = arg.arguments?.[1];
    if (init === undefined) return true;
    if (!ts.isObjectLiteralExpression(init)) return true;
    return propertyNamed(init, 'body') !== undefined || init.properties.some(ts.isSpreadAssignment);
  }
  return ts.isIdentifier(arg) && requestTyped.has(arg.text);
}

function stringConstants(sf: ts.SourceFile): Map<string, string> {
  const constants = new Map<string, string>();
  sf.forEachChild((node) => {
    if (!ts.isVariableStatement(node)) return;
    for (const d of node.declarationList.declarations) {
      if (
        ts.isIdentifier(d.name) &&
        d.initializer !== undefined &&
        ts.isStringLiteralLike(d.initializer)
      ) {
        constants.set(d.name.text, d.initializer.text);
      }
    }
  });
  return constants;
}

export function scanSource(
  file: string,
  text: string,
  routes: ReadonlyMap<string, string>,
  caps: Readonly<Record<string, number>>,
  allow: Readonly<Record<string, string>>,
): { findings: Finding[]; usedRoutes: Set<string> } {
  const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const constants = stringConstants(sf);
  const aliases = fetchAliases(sf);
  const requestTyped = requestTypedNames(sf);
  const findings: Finding[] = [];
  const usedRoutes = new Set<string>();
  const report = (node: ts.Node, kind: string, detail: string): void => {
    findings.push({ file, function: enclosingFunction(node), kind, detail });
  };

  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node)) {
      const callee = node.expression;
      const arg = node.arguments[0];
      const object = arg !== undefined && ts.isObjectLiteralExpression(arg) ? arg : undefined;
      let route: string | undefined;
      let named = false;
      if (ts.isIdentifier(callee) && routes.has(callee.text)) {
        route = routes.get(callee.text);
        named = true;
      } else if (
        ts.isPropertyAccessExpression(callee) &&
        verbs.has(callee.name.text) &&
        object !== undefined
      ) {
        const url = propertyNamed(object, 'url');
        if (url !== undefined && ts.isPropertyAssignment(url)) {
          const value = ts.isStringLiteralLike(url.initializer)
            ? url.initializer.text
            : ts.isIdentifier(url.initializer)
              ? constants.get(url.initializer.text)
              : undefined;
          route = value === undefined ? undefined : `${callee.name.text.toUpperCase()} ${value}`;
          named = true;
          if (route === undefined) report(node, 'unresolved-url', callee.getText());
        }
      }
      if (named && object !== undefined && route !== undefined) {
        const hasSpread = object.properties.some(ts.isSpreadAssignment);
        if (hasSpread) report(node, 'unprovable-spread', route);
        if (propertyNamed(object, 'body') !== undefined) {
          usedRoutes.add(route);
          if (caps[route] === undefined) report(node, 'uncapped-route', route);
        }
      }
      const second = node.arguments[1];
      // `fetch` takes at most two arguments, so a longer fetch-named call is not a send.
      if (isFetchCallee(callee, aliases) && node.arguments.length <= 2) {
        const init =
          second !== undefined && ts.isObjectLiteralExpression(second) ? second : undefined;
        const carries =
          second !== undefined
            ? init === undefined ||
              propertyNamed(init, 'body') !== undefined ||
              init.properties.some(ts.isSpreadAssignment)
            : requestCarriesBody(node.arguments[0], requestTyped);
        const key = `${file}#${enclosingFunction(node)}`;
        if (carries && allow[key] === undefined) report(node, 'uncapped-fetch', key);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return { findings, usedRoutes };
}

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

    it('normalizes quoted and computed client route keys', () => {
      expect(
        scan("client.post({ ['url']: '/widgets', 'body': 'x' })").map((finding) => finding.kind),
      ).toEqual(['uncapped-route']);
    });

    it.each([
      'fetch(u)',
      "fetch('/widgets')",
      'fetch(new Request(u, { method: "GET" }))',
      'const request = new Request(u, { method: "GET" }); fetch(request)',
    ])('keeps the provably body-free form %s clean', (statement) => {
      expect(scan(`function get(u: string) { ${statement}; }`)).toEqual([]);
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
