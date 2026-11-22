/*
 * The alias analysis behind requestCapScan.testing.ts: it decides how an expression names a fetch
 * (`fetch`, a tracked alias, an object or class property holding one) and which names are bound
 * to one. The `Request` analysis lives in requestCapScan.requests.testing.ts.
 */
import ts from 'typescript';

/** Names bound to a fetch, and static property names that hold one. */
export type FetchRefs = { names: ReadonlySet<string>; properties: ReadonlySet<string> };

/** The static name of a property, including `'body'` and computed `['body']` keys. */
export function propertyName(name: ts.PropertyName): string | undefined {
  if (ts.isComputedPropertyName(name)) {
    return ts.isStringLiteralLike(name.expression) ? name.expression.text : undefined;
  }
  if (ts.isIdentifier(name) || ts.isStringLiteralLike(name) || ts.isNumericLiteral(name)) {
    return name.text;
  }
  return undefined;
}

export function propertyNamed(
  arg: ts.ObjectLiteralExpression,
  name: string,
): ts.ObjectLiteralElementLike | undefined {
  return arg.properties.find((p) => p.name !== undefined && propertyName(p.name) === name);
}

/** The static member name of `a.b`, `a['b']` or ``a[`b`]``; undefined for anything else. */
export function memberName(node: ts.Expression): string | undefined {
  if (ts.isPropertyAccessExpression(node)) return node.name.text;
  if (ts.isElementAccessExpression(node) && ts.isStringLiteralLike(node.argumentExpression)) {
    return node.argumentExpression.text;
  }
  return undefined;
}

/** Whether a name is a fetch: `fetch`, `fetchImpl`, `o.fetch`, `deps.fetch`, ... */
function fetchLikeName(text: string): boolean {
  return /fetch/i.test(text);
}

/** Strips parentheses, `as`, `satisfies`, `!`, type assertions and a comma list's last item. */
export function unwrap(node: ts.Expression): ts.Expression {
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

/** `fetch.bind(thisArg)` / `fetch['bind'](thisArg)` target, or undefined. */
function bindTarget(node: ts.Expression, refs: FetchRefs): ts.Expression | undefined {
  if (!ts.isCallExpression(node)) return undefined;
  const callee = unwrap(node.expression);
  if (memberName(callee) !== 'bind') return undefined;
  const target =
    ts.isPropertyAccessExpression(callee) || ts.isElementAccessExpression(callee)
      ? callee.expression
      : undefined;
  return target !== undefined && fetchKind(target, refs) !== undefined ? target : undefined;
}

/**
 * How an expression names a fetch: `exact` (`fetch`, a tracked alias or a property holding one),
 * `named` (only the name contains "fetch"), or undefined. Only an exact fetch takes two arguments.
 */
export function fetchKind(node: ts.Expression, refs: FetchRefs): 'exact' | 'named' | undefined {
  const e = unwrap(node);
  if (ts.isIdentifier(e)) {
    if (e.text === 'fetch' || refs.names.has(e.text)) return 'exact';
    return fetchLikeName(e.text) ? 'named' : undefined;
  }
  const name = memberName(e);
  if (name !== undefined) {
    if (name === 'fetch' || refs.properties.has(name)) return 'exact';
    return fetchLikeName(name) ? 'named' : undefined;
  }
  if (bindTarget(e, refs) !== undefined) return 'exact';
  if (ts.isBinaryExpression(e)) {
    const kind = e.operatorToken.kind;
    const logical =
      kind === ts.SyntaxKind.QuestionQuestionToken ||
      kind === ts.SyntaxKind.BarBarToken ||
      kind === ts.SyntaxKind.AmpersandAmpersandToken;
    if (!logical) return undefined;
    const left = fetchKind(e.left, refs);
    const right = fetchKind(e.right, refs);
    if (left === 'exact' || right === 'exact') return 'exact';
    return left ?? right;
  }
  if (ts.isConditionalExpression(e)) {
    const whenTrue = fetchKind(e.whenTrue, refs);
    const whenFalse = fetchKind(e.whenFalse, refs);
    if (whenTrue === 'exact' || whenFalse === 'exact') return 'exact';
    return whenTrue ?? whenFalse;
  }
  return undefined;
}

type AliasState = { names: Set<string>; properties: Set<string>; changed: boolean };

function addName(state: AliasState, name: string): void {
  if (state.names.has(name)) return;
  state.names.add(name);
  state.changed = true;
}

function addProperty(state: AliasState, name: string): void {
  if (state.properties.has(name)) return;
  state.properties.add(name);
  state.changed = true;
}

/** `const f = fetch`, a parameter default holding a fetch. */
function aliasFromDeclaration(node: ts.Node, state: AliasState): void {
  if (!(ts.isVariableDeclaration(node) || ts.isParameter(node)) || !ts.isIdentifier(node.name)) {
    return;
  }
  const init = node.initializer;
  if (init !== undefined && fetchKind(init, state) !== undefined) addName(state, node.name.text);
}

/** `f = fetch`, `o.send = fetch`. */
function aliasFromAssignment(node: ts.Node, state: AliasState): void {
  if (
    !ts.isBinaryExpression(node) ||
    node.operatorToken.kind !== ts.SyntaxKind.EqualsToken ||
    fetchKind(node.right, state) === undefined
  ) {
    return;
  }
  if (ts.isIdentifier(node.left)) addName(state, node.left.text);
  const property = memberName(node.left);
  if (property !== undefined) addProperty(state, property);
}

/** `class C { send = fetch }`, `{ send: fetch }`. */
function aliasFromProperty(node: ts.Node, state: AliasState): void {
  if (!(ts.isPropertyDeclaration(node) || ts.isPropertyAssignment(node)) || !node.initializer) {
    return;
  }
  const property = propertyName(node.name);
  if (property !== undefined && fetchKind(node.initializer, state) !== undefined) {
    addProperty(state, property);
  }
}

/** `const { fetch: send } = deps`: a destructured key named like a fetch. */
function aliasFromBindingPattern(node: ts.Node, state: AliasState): void {
  if (!ts.isObjectBindingPattern(node)) return;
  for (const element of node.elements) {
    const source = element.propertyName ?? element.name;
    const key = ts.isIdentifier(source) || ts.isStringLiteralLike(source) ? source.text : undefined;
    if (key !== undefined && fetchLikeName(key) && ts.isIdentifier(element.name)) {
      addName(state, element.name.text);
    }
  }
}

/** Fetch aliases and property names holding one, collected in source order to a fixpoint. */
export function fetchAliases(sf: ts.SourceFile): { names: Set<string>; properties: Set<string> } {
  const state: AliasState = { names: new Set(), properties: new Set(), changed: true };
  const walk = (node: ts.Node): void => {
    aliasFromDeclaration(node, state);
    aliasFromAssignment(node, state);
    aliasFromProperty(node, state);
    aliasFromBindingPattern(node, state);
    ts.forEachChild(node, walk);
  };
  while (state.changed) {
    state.changed = false;
    walk(sf);
  }
  return { names: state.names, properties: state.properties };
}
