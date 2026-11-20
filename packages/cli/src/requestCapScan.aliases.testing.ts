/*
 * The alias and `Request` analysis behind requestCapScan.testing.ts: it decides how an expression
 * names a fetch (`fetch`, a tracked alias, an object or class property holding one), which names
 * are bound to one, and whether a `new Request` may already carry a body. It is a module of its
 * own only to keep each file under the 500-line limit.
 */
import ts from 'typescript';

/** Names bound to a fetch, and static property names that hold one. */
export type FetchRefs = { names: ReadonlySet<string>; properties: ReadonlySet<string> };

/** Identifiers typed `Request` with no initializer, and the initializers of the typed ones. */
export type RequestTyped = { bare: Set<string>; initialized: Map<string, ts.Expression> };

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

/** Fetch aliases and property names holding one, collected in source order to a fixpoint. */
export function fetchAliases(sf: ts.SourceFile): { names: Set<string>; properties: Set<string> } {
  const names = new Set<string>();
  const properties = new Set<string>();
  let changed = true;
  const walk = (node: ts.Node): void => {
    const refs: FetchRefs = { names, properties };
    if ((ts.isVariableDeclaration(node) || ts.isParameter(node)) && ts.isIdentifier(node.name)) {
      const name = node.name.text;
      const init = node.initializer;
      if (init !== undefined && !names.has(name) && fetchKind(init, refs) !== undefined) {
        names.add(name);
        changed = true;
      }
    }
    if (
      ts.isBinaryExpression(node) &&
      node.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
      fetchKind(node.right, refs) !== undefined
    ) {
      const property = memberName(node.left);
      if (ts.isIdentifier(node.left) && !names.has(node.left.text)) {
        names.add(node.left.text);
        changed = true;
      }
      if (property !== undefined && !properties.has(property)) {
        properties.add(property);
        changed = true;
      }
    }
    if ((ts.isPropertyDeclaration(node) || ts.isPropertyAssignment(node)) && node.initializer) {
      const property = propertyName(node.name);
      const init = node.initializer;
      if (
        property !== undefined &&
        !properties.has(property) &&
        fetchKind(init, refs) !== undefined
      ) {
        properties.add(property);
        changed = true;
      }
    }
    if (ts.isObjectBindingPattern(node)) {
      for (const element of node.elements) {
        const source = element.propertyName ?? element.name;
        const key =
          ts.isIdentifier(source) || ts.isStringLiteralLike(source) ? source.text : undefined;
        if (
          key !== undefined &&
          fetchLikeName(key) &&
          ts.isIdentifier(element.name) &&
          !names.has(element.name.text)
        ) {
          names.add(element.name.text);
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
  return { names, properties };
}

/** Whether a type node names `Request`: `Request`, `ns.Request`, or a union containing one. */
function isRequestTypeNode(node: ts.TypeNode | undefined): boolean {
  if (node === undefined) return false;
  if (ts.isUnionTypeNode(node)) return node.types.some(isRequestTypeNode);
  if (ts.isParenthesizedTypeNode(node)) return isRequestTypeNode(node.type);
  return /(^|\.)Request$/.test(node.getText());
}

export function requestTypedNames(sf: ts.SourceFile): RequestTyped {
  const bare = new Set<string>();
  const initialized = new Map<string, ts.Expression>();
  const walk = (node: ts.Node): void => {
    if (
      (ts.isParameter(node) || ts.isVariableDeclaration(node)) &&
      ts.isIdentifier(node.name) &&
      isRequestTypeNode(node.type)
    ) {
      const name = node.name.text;
      if (node.initializer === undefined) bare.add(name);
      else if (!initialized.has(name)) initialized.set(name, node.initializer);
    }
    ts.forEachChild(node, walk);
  };
  walk(sf);
  return { bare, initialized };
}

/** Identifiers bound to the `Request` constructor, to a fixpoint: `const R = Request`. */
export function requestConstructorAliases(sf: ts.SourceFile): Set<string> {
  const aliases = new Set<string>();
  const isConstructor = (node: ts.Expression): boolean => {
    const e = unwrap(node);
    if (ts.isIdentifier(e)) return e.text === 'Request' || aliases.has(e.text);
    return memberName(e) === 'Request';
  };
  let changed = true;
  const walk = (node: ts.Node): void => {
    if (
      (ts.isVariableDeclaration(node) || ts.isParameter(node)) &&
      ts.isIdentifier(node.name) &&
      node.initializer !== undefined &&
      !aliases.has(node.name.text) &&
      isConstructor(node.initializer)
    ) {
      aliases.add(node.name.text);
      changed = true;
    }
    ts.forEachChild(node, walk);
  };
  while (changed) {
    changed = false;
    walk(sf);
  }
  return aliases;
}

export function isRequestConstruction(
  node: ts.Node,
  constructors: ReadonlySet<string>,
): node is ts.NewExpression {
  if (!ts.isNewExpression(node)) return false;
  const callee = unwrap(node.expression);
  if (ts.isIdentifier(callee)) return callee.text === 'Request' || constructors.has(callee.text);
  return memberName(callee) === 'Request';
}

/** Why a `Request`-typed value may already carry a body, or undefined. */
function inheritedBodyReason(
  input: ts.Expression,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
): string | undefined {
  const source = unwrap(input);
  if (!ts.isIdentifier(source)) return undefined;
  if (typed.bare.has(source.text)) return 'Request inherits a body';
  const init = typed.initialized.get(source.text);
  if (init === undefined) return undefined;
  const construction = unwrap(init);
  return isRequestConstruction(construction, constructors)
    ? requestBodyReason(construction, typed, constructors)
    : undefined;
}

/** Why a `Request`-typed identifier may carry a body, or undefined when its own init denies it. */
export function typedRequestReason(
  id: ts.Identifier,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
): string | undefined {
  const init = typed.initialized.get(id.text);
  if (init === undefined) {
    return typed.bare.has(id.text) ? 'Request-typed first argument' : undefined;
  }
  const construction = unwrap(init);
  return isRequestConstruction(construction, constructors)
    ? requestBodyReason(construction, typed, constructors)
    : undefined;
}

/** Why a `new Request(input, init)` may carry a body, or undefined when `init` proves it cannot. */
export function requestBodyReason(
  node: ts.NewExpression,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
): string | undefined {
  const input = node.arguments?.[0];
  if (input !== undefined) {
    const inherited = inheritedBodyReason(input, typed, constructors);
    if (inherited !== undefined) return inherited;
  }
  const init = node.arguments?.[1];
  if (init === undefined) return 'Request without a visible init';
  const plain = unwrap(init);
  if (!ts.isObjectLiteralExpression(plain)) return 'Request init is not an object literal';
  if (propertyNamed(plain, 'body') !== undefined) return 'Request with a body';
  if (plain.properties.some(ts.isSpreadAssignment)) return 'Request init with a spread';
  return undefined;
}
