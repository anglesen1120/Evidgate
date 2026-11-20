/*
 * The syntax scan behind requestCapScan.test.ts: finds every site that sends a request body
 * and reports the ones without a cap. Three kinds of site exist: a generated SDK operation
 * (or the client's own `post`) called with a `body`, whose `METHOD route` must be a row of the
 * cap table; a `fetch` call that carries a body, and a `Request` built with one, which bypass
 * the client and are allowed only in the functions of the allow-list; and any other
 * transport (`node:http(s)`, `undici`, `XMLHttpRequest`, `sendBeacon`), which is refused.
 *
 * The scan fails closed: a form it cannot prove body-free is reported, not passed. It follows
 * a fetch through aliases (`const f = fetch`, `??`, `||`, a ternary, destructuring, a default
 * parameter, `as`, comma and parentheses), `.call`, `.apply` and `.bind`, and a `Request`
 * built with a body, wherever the `Request` goes.
 *
 * What syntax cannot decide, and stays a named limit: a body built in another module and
 * handed to a fetch-like helper that is not named like one, a fetch alias imported from
 * another file, and a property or callee chosen by a computed (non-literal) name.
 */
import ts from 'typescript';

export type Finding = { file: string; function: string; kind: string; detail: string };
const verbs = new Set(['post', 'put', 'patch', 'delete']);
const otherTransportModules = /^(node:)?(https?|http2|undici)$/;
const otherTransportGlobals = new Set(['XMLHttpRequest', 'sendBeacon']);

/** `operationName -> "METHOD /path/{param}"` read from the generated operations file. */
export function operationRoutes(text: string): Map<string, string> {
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

/** Whether a name is a fetch: `fetch`, `fetchImpl`, `o.fetch`, `deps.fetch`, ... */
function fetchLikeName(text: string): boolean {
  return /fetch/i.test(text);
}

/** Strips parentheses, `as`, `satisfies`, `!`, type assertions and a comma list's last item. */
function unwrap(node: ts.Expression): ts.Expression {
  let current = node;
  for (;;) {
    if (
      ts.isParenthesizedExpression(current) ||
      ts.isAsExpression(current) ||
      ts.isSatisfiesExpression(current) ||
      ts.isNonNullExpression(current) ||
      ts.isTypeAssertionExpression(current)
    ) {
      current = current.expression;
    } else if (
      ts.isBinaryExpression(current) &&
      current.operatorToken.kind === ts.SyntaxKind.CommaToken
    ) {
      current = current.right;
    } else {
      return current;
    }
  }
}

/** Whether an expression evaluates to a fetch, through names, aliases and `.bind`. */
function isFetchExpression(node: ts.Expression, aliases: ReadonlySet<string>): boolean {
  const e = unwrap(node);
  if (ts.isIdentifier(e)) return fetchLikeName(e.text) || aliases.has(e.text);
  if (ts.isPropertyAccessExpression(e)) return fetchLikeName(e.name.text);
  if (ts.isElementAccessExpression(e)) {
    return ts.isStringLiteralLike(e.argumentExpression) && fetchLikeName(e.argumentExpression.text);
  }
  if (ts.isBinaryExpression(e)) {
    const kind = e.operatorToken.kind;
    const logical =
      kind === ts.SyntaxKind.QuestionQuestionToken ||
      kind === ts.SyntaxKind.BarBarToken ||
      kind === ts.SyntaxKind.AmpersandAmpersandToken;
    return logical && (isFetchExpression(e.left, aliases) || isFetchExpression(e.right, aliases));
  }
  if (ts.isConditionalExpression(e)) {
    return isFetchExpression(e.whenTrue, aliases) || isFetchExpression(e.whenFalse, aliases);
  }
  return (
    ts.isCallExpression(e) &&
    ts.isPropertyAccessExpression(e.expression) &&
    e.expression.name.text === 'bind' &&
    isFetchExpression(e.expression.expression, aliases)
  );
}

/** Names bound to a fetch, to a fixpoint: chained aliases, destructuring, defaults, assignments. */
function fetchAliases(sf: ts.SourceFile): Set<string> {
  const aliases = new Set<string>();
  const add = (name: string): void => {
    aliases.add(name);
  };
  let changed = true;
  const walk = (node: ts.Node): void => {
    if (
      (ts.isVariableDeclaration(node) || ts.isParameter(node)) &&
      node.initializer !== undefined &&
      ts.isIdentifier(node.name) &&
      !aliases.has(node.name.text) &&
      isFetchExpression(node.initializer, aliases)
    ) {
      add(node.name.text);
      changed = true;
    }
    if (
      ts.isBinaryExpression(node) &&
      node.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
      ts.isIdentifier(node.left) &&
      !aliases.has(node.left.text) &&
      isFetchExpression(node.right, aliases)
    ) {
      add(node.left.text);
      changed = true;
    }
    if (ts.isObjectBindingPattern(node)) {
      for (const element of node.elements) {
        const source = element.propertyName ?? element.name;
        const key = ts.isIdentifier(source)
          ? source.text
          : ts.isStringLiteralLike(source)
            ? source.text
            : undefined;
        if (
          key !== undefined &&
          fetchLikeName(key) &&
          ts.isIdentifier(element.name) &&
          !aliases.has(element.name.text)
        ) {
          add(element.name.text);
          changed = true;
        }
      }
    }
    ts.forEachChild(node, walk);
  };
  while (changed) {
    changed = false;
    walk(sf);
  }
  return aliases;
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

function isRequestConstruction(node: ts.Node): node is ts.NewExpression {
  return (
    ts.isNewExpression(node) &&
    ts.isIdentifier(node.expression) &&
    node.expression.text === 'Request'
  );
}

/** Why a `new Request(input, init)` may carry a body, or undefined when `init` proves it cannot. */
function requestBodyReason(node: ts.NewExpression): string | undefined {
  const init = node.arguments?.[1];
  if (init === undefined) return 'Request without a visible init';
  if (!ts.isObjectLiteralExpression(init)) return 'Request init is not an object literal';
  if (propertyNamed(init, 'body') !== undefined) return 'Request with a body';
  if (init.properties.some(ts.isSpreadAssignment)) return 'Request init with a spread';
  return undefined;
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
  const aliases = fetchAliases(sf);
  const requestTyped = requestTypedNames(sf);
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

  /** The fetch callee of a call and how the arguments are reached, or undefined. */
  const fetchCall = (
    call: ts.CallExpression,
  ): { form: string; via: 'call' | 'apply' | undefined } | undefined => {
    const callee = unwrap(call.expression);
    if (
      ts.isPropertyAccessExpression(callee) &&
      (callee.name.text === 'call' || callee.name.text === 'apply') &&
      isFetchExpression(callee.expression, aliases)
    ) {
      return { form: `${callee.expression.getText()}.${callee.name.text}`, via: callee.name.text };
    }
    return isFetchExpression(callee, aliases)
      ? { form: callee.getText().slice(0, 60), via: undefined }
      : undefined;
  };

  const checkFetch = (
    call: ts.CallExpression,
    target: { form: string; via: 'call' | 'apply' | undefined },
  ): void => {
    const args = effectiveArguments(call, target.via);
    const send = (kind: string, why: string): void => reportSend(call, kind, target.form, why);
    if (args === undefined) return send('unresolved-fetch', 'arguments are not an array literal');
    // `fetch` takes at most two arguments, so a longer fetch-named call is not a send.
    if (args.length > 2) return;
    if (args.some(ts.isSpreadElement)) return send('unresolved-fetch', 'spread argument');
    const first = args[0] === undefined ? undefined : unwrap(args[0]);
    const second = args[1] === undefined ? undefined : unwrap(args[1]);
    if (first !== undefined && isRequestConstruction(first)) {
      handled.add(first);
      const why = requestBodyReason(first);
      if (why !== undefined) return send('uncapped-fetch', `${why} as the first argument`);
    } else if (first !== undefined && ts.isIdentifier(first) && requestTyped.has(first.text)) {
      return send('uncapped-fetch', 'Request-typed first argument');
    } else if (first !== undefined && (ts.isCallExpression(first) || ts.isAwaitExpression(first))) {
      return send('unresolved-fetch', 'first argument is computed');
    }
    if (second === undefined) return;
    if (!ts.isObjectLiteralExpression(second)) {
      return send('uncapped-fetch', 'init is not an object literal');
    }
    if (propertyNamed(second, 'body') !== undefined) return send('uncapped-fetch', 'body in init');
    if (second.properties.some(ts.isSpreadAssignment)) {
      send('uncapped-fetch', 'spread in init');
    }
  };

  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteralLike(node.moduleSpecifier)) {
      const name = node.moduleSpecifier.text;
      if (otherTransportModules.test(name))
        reportSend(node, 'other-transport', `import ${name}`, 'module');
    }
    if (ts.isIdentifier(node) && otherTransportGlobals.has(node.text)) {
      reportSend(node, 'other-transport', node.text, 'global');
    }
    if (isRequestConstruction(node) && !handled.has(node)) {
      const why = requestBodyReason(node);
      if (why !== undefined) reportSend(node, 'uncapped-request', 'new Request', why);
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
      const target = fetchCall(node);
      if (target !== undefined) checkFetch(node, target);
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return { findings, usedRoutes };
}
