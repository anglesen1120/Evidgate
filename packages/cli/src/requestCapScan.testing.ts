/*
 * The syntax scan behind requestCapScan.test.ts: it finds every site that sends a request body
 * and reports the ones without a cap. An SDK operation (or the client's own `post`) called with
 * a `body` must be a row of the cap table; a `fetch` call carrying a body and a `Request` built
 * with one are allowed only in the allow-list's functions; any other transport (`node:http(s)`,
 * `node:http2`, `node:net`, `node:tls`, `node:dgram`, `undici`, `node-fetch`, `axios`, `got`,
 * `ws`, `XMLHttpRequest`, `sendBeacon`) is refused, and a subpath of a transport module
 * (`node:http/promises`, `axios/lib/x`) is the same transport.
 *
 * The scan fails closed: a form it cannot prove body-free is reported, not passed. The alias and
 * `Request` analysis lives in requestCapScan.aliases.testing.ts; it follows a fetch through
 * aliases (`const f = fetch`, assignment, `??`, `||`, `&&`, a ternary, destructuring with a
 * literal key, a default parameter, `as`, `satisfies`, `!`, comma and parentheses), object and
 * class properties holding one, `.call` and `.apply` (also `fetch['call']`), `.bind` without
 * bound arguments, element access, and a `Request` built with a body wherever it goes.
 *
 * An allow-list entry is keyed by `file#function` and exempts only the findings the send path
 * reports there: a direct fetch, a `Request` construction, an unresolved fetch, another
 * transport. It does not exempt SDK-route findings (`uncapped-route`, `unprovable-args`,
 * `unprovable-spread`, `unresolved-url`), which name a route rather than a send.
 *
 * Named limits: a body built in another module and handed to a fetch-like helper not named like
 * one; a fetch alias imported from another file; alias and property names, which are file-wide,
 * so a same-named local that never holds a fetch is still treated as one (fail closed); an SDK
 * operation reached through an import alias or namespace object; a callee chosen by a computed
 * (non-literal) name; a fetch-named call (not `fetch` itself, not a tracked alias) with more
 * than two arguments, read as another function (`fetchActionsIdToken(config, fetchImpl, deps)`);
 * an inherited body reaching `new Request(input, init)` through a non-identifier input
 * (`r.clone()`, a conditional, an alias of an alias), which is not followed; and an exotic call
 * such as `Function.prototype.call.call(fetch, ...)`.
 */
import ts from 'typescript';

import {
  fetchAliases,
  fetchKind,
  type FetchRefs,
  isRequestConstruction,
  memberName,
  propertyNamed,
  requestBodyReason,
  requestConstructorAliases,
  type RequestTyped,
  requestTypedNames,
  typedRequestReason,
  unwrap,
} from './requestCapScan.aliases.testing.js';

export type Finding = { file: string; function: string; kind: string; detail: string };
const verbs: Readonly<Record<string, true>> = { post: true, put: true, patch: true, delete: true };
const otherTransportModules =
  /^(node:)?(https?|http2|net|tls|dgram|undici|node-fetch|axios|got|ws)(\/.*)?$/;
const otherTransportGlobals: Readonly<Record<string, true>> = {
  XMLHttpRequest: true,
  sendBeacon: true,
};

/** `operationName -> "METHOD /path/{param}"` read from the generated operations file. */
export function operationRoutes(text: string): Map<string, string> {
  const routes = new Map<string, string>();
  for (const chunk of text.split('export const ').slice(1)) {
    const match = /^(\w+) = [\s\S]*?\)\.(post|put|patch|delete)<[\s\S]*?\burl: '([^']+)'/.exec(
      chunk,
    );
    if (match === null) continue;
    const name = match[1];
    const verb = match[2];
    const url = match[3];
    if (name === undefined || verb === undefined || url === undefined) continue;
    routes.set(name, `${verb.toUpperCase()} ${url}`);
  }
  return routes;
}

export function enclosingFunction(node: ts.Node): string {
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

/** The arguments a call passes to the fetch, resolving `.call` and `.apply`; undefined = unresolved. */
function effectiveArguments(
  call: ts.CallExpression,
  viaMethod: 'call' | 'apply' | undefined,
): readonly ts.Expression[] | undefined {
  if (viaMethod === undefined) return call.arguments;
  if (viaMethod === 'call') return call.arguments.slice(1);
  const list = call.arguments[1];
  if (list === undefined) return [];
  const unwrapped = unwrap(list);
  return ts.isArrayLiteralExpression(unwrapped) ? unwrapped.elements : undefined;
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
  const aliasSets = fetchAliases(sf);
  const refs: FetchRefs = { names: aliasSets.names, properties: aliasSets.properties };
  const requestTyped: RequestTyped = requestTypedNames(sf);
  const constructors = requestConstructorAliases(sf);
  const findings: Finding[] = [];
  const usedRoutes = new Set<string>();
  const handled = new Set<ts.Node>();
  const report = (node: ts.Node, kind: string, detail: string): void => {
    findings.push({ file, function: enclosingFunction(node), kind, detail });
  };
  const reportSend = (node: ts.Node, kind: string, form: string, why: string): void => {
    const key = `${file}#${enclosingFunction(node)}`;
    if (allow[key] === undefined) report(node, kind, `${key}: ${form} (${why})`);
  };

  /** The fetch callee of a call and how its arguments are reached, or undefined. */
  const fetchCall = (
    call: ts.CallExpression,
  ): { form: string; via: 'call' | 'apply' | undefined; exact: boolean } | undefined => {
    const callee = unwrap(call.expression);
    const name = memberName(callee);
    const receiver =
      ts.isPropertyAccessExpression(callee) || ts.isElementAccessExpression(callee)
        ? callee.expression
        : undefined;
    if ((name === 'call' || name === 'apply') && receiver !== undefined) {
      const kind = fetchKind(receiver, refs);
      if (kind !== undefined) {
        return { form: `${receiver.getText()}.${name}`, via: name, exact: kind === 'exact' };
      }
    }
    const kind = fetchKind(callee, refs);
    return kind === undefined
      ? undefined
      : { form: callee.getText().slice(0, 60), via: undefined, exact: kind === 'exact' };
  };

  /** The fetch handed to `Reflect.apply`, whose argument list syntax cannot read, or undefined. */
  const reflectApplyTarget = (call: ts.CallExpression): ts.Expression | undefined => {
    const callee = unwrap(call.expression);
    if (memberName(callee) !== 'apply') return undefined;
    if (!ts.isPropertyAccessExpression(callee) && !ts.isElementAccessExpression(callee)) {
      return undefined;
    }
    const owner = unwrap(callee.expression);
    const ownerName = ts.isIdentifier(owner) ? owner.text : memberName(owner);
    if (ownerName !== 'Reflect') return undefined;
    const target = call.arguments[0];
    if (target === undefined || fetchKind(unwrap(target), refs) === undefined) return undefined;
    return target;
  };

  const checkFetch = (
    call: ts.CallExpression,
    target: { form: string; via: 'call' | 'apply' | undefined; exact: boolean },
  ): void => {
    const args = effectiveArguments(call, target.via);
    const send = (kind: string, why: string): void => reportSend(call, kind, target.form, why);
    if (args === undefined) return send('unresolved-fetch', 'arguments are not an array literal');
    // Only `fetch`, a tracked alias or `.call`/`.apply` on one takes exactly two arguments; a
    // longer fetch-named helper is read as another function (see the header's named limits).
    if (!target.exact && args.length > 2) return;
    if (args.some(ts.isSpreadElement)) return send('unresolved-fetch', 'spread argument');
    const first = args[0] === undefined ? undefined : unwrap(args[0]);
    const second = args[1] === undefined ? undefined : unwrap(args[1]);
    if (first !== undefined) {
      if (isRequestConstruction(first, constructors)) {
        handled.add(first);
        const why = requestBodyReason(first, requestTyped, constructors);
        if (why !== undefined) return send('uncapped-fetch', `${why} as the first argument`);
      } else if (ts.isIdentifier(first)) {
        const why = typedRequestReason(first, requestTyped, constructors);
        if (why !== undefined) return send('uncapped-fetch', why);
      } else if (ts.isCallExpression(first) || ts.isAwaitExpression(first)) {
        return send('unresolved-fetch', 'first argument is computed');
      }
    }
    if (second === undefined) return;
    if (!ts.isObjectLiteralExpression(second)) {
      return send('uncapped-fetch', 'init is not an object literal');
    }
    if (propertyNamed(second, 'body') !== undefined) return send('uncapped-fetch', 'body in init');
    if (second.properties.some(ts.isSpreadAssignment)) send('uncapped-fetch', 'spread in init');
  };

  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteralLike(node.moduleSpecifier)) {
      const name = node.moduleSpecifier.text;
      if (otherTransportModules.test(name)) {
        reportSend(node, 'other-transport', `import ${name}`, 'module');
      }
    }
    if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteralLike(node.moduleSpecifier) &&
      otherTransportModules.test(node.moduleSpecifier.text)
    ) {
      const name = node.moduleSpecifier.text;
      reportSend(node, 'other-transport', `export ${name}`, 'module');
    }
    if (
      ts.isImportEqualsDeclaration(node) &&
      ts.isExternalModuleReference(node.moduleReference) &&
      ts.isStringLiteralLike(node.moduleReference.expression) &&
      otherTransportModules.test(node.moduleReference.expression.text)
    ) {
      const name = node.moduleReference.expression.text;
      reportSend(node, 'other-transport', `import ${name}`, 'module');
    }
    if (ts.isIdentifier(node) && Object.hasOwn(otherTransportGlobals, node.text)) {
      reportSend(node, 'other-transport', node.text, 'global');
    }
    const member = ts.isElementAccessExpression(node) ? memberName(node) : undefined;
    if (member !== undefined && Object.hasOwn(otherTransportGlobals, member)) {
      reportSend(node, 'other-transport', member, 'global');
    }
    if (isRequestConstruction(node, constructors) && !handled.has(node)) {
      const why = requestBodyReason(node, requestTyped, constructors);
      if (why !== undefined) reportSend(node, 'uncapped-request', 'new Request', why);
    }
    if (ts.isTaggedTemplateExpression(node) && fetchKind(node.tag, refs) !== undefined) {
      reportSend(node, 'unresolved-fetch', node.tag.getText().slice(0, 60), 'tagged template');
    }
    if (ts.isCallExpression(node)) {
      const callee = node.expression;
      const arg = node.arguments[0];
      const stringArg = arg !== undefined && ts.isStringLiteralLike(arg) ? arg.text : undefined;
      const isRequire = ts.isIdentifier(callee) && callee.text === 'require';
      if (
        (isRequire || callee.kind === ts.SyntaxKind.ImportKeyword) &&
        stringArg !== undefined &&
        otherTransportModules.test(stringArg)
      ) {
        reportSend(
          node,
          'other-transport',
          `${isRequire ? 'require' : 'import()'} ${stringArg}`,
          'module',
        );
      }
      const boundCallee = unwrap(callee);
      const boundTarget =
        ts.isPropertyAccessExpression(boundCallee) || ts.isElementAccessExpression(boundCallee)
          ? boundCallee.expression
          : undefined;
      if (
        memberName(boundCallee) === 'bind' &&
        boundTarget !== undefined &&
        fetchKind(boundTarget, refs) !== undefined &&
        node.arguments.length > 1
      ) {
        reportSend(node, 'unresolved-fetch', `${boundTarget.getText()}.bind`, 'bound arguments');
      }
      if (reflectApplyTarget(node) !== undefined) {
        reportSend(node, 'unresolved-fetch', 'Reflect.apply', 'target is a fetch');
      }
      const plain = arg === undefined ? undefined : unwrap(arg);
      const object = plain !== undefined && ts.isObjectLiteralExpression(plain) ? plain : undefined;
      let route: string | undefined;
      let named = false;
      if (ts.isIdentifier(callee) && routes.has(callee.text)) {
        route = routes.get(callee.text);
        named = true;
        if (object === undefined) report(node, 'unprovable-args', `${callee.text} without options`);
      } else if (ts.isPropertyAccessExpression(callee) && Object.hasOwn(verbs, callee.name.text)) {
        if (object === undefined && /client/i.test(callee.expression.getText())) {
          report(node, 'unprovable-args', `${callee.getText()} without options`);
        }
        const url = object === undefined ? undefined : propertyNamed(object, 'url');
        if (object !== undefined && url !== undefined && ts.isPropertyAssignment(url)) {
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
      const target = fetchCall(node);
      if (target !== undefined) checkFetch(node, target);
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return { findings, usedRoutes };
}
