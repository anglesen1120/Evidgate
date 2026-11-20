/*
 * The `Request` analysis behind requestCapScan.testing.ts: why a `Request` input may carry a body,
 * followed through constructions, aliases and assignments. The declarations it reads come from
 * requestCapScan.declarations.testing.ts.
 */
import ts from 'typescript';

import { memberName, propertyNamed, unwrap } from './requestCapScan.aliases.testing.js';
import {
  type Declaration,
  resolveDeclarations,
  type RequestTyped,
} from './requestCapScan.declarations.testing.js';

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

const reassignedReason = 'Request-typed value reassigned after its declaration';
const opaqueReason = 'Request-typed name rebound by destructuring';
const unknownReason = 'Request-typed name rebound by an untyped declaration';
const unprovenReason = 'Request-typed value without a visible Request construction';

/**
 * Why a name used as a `Request` input may carry a body, or undefined. The use resolves to the
 * declarations of the nearest scope declaring the name and every one is checked: a typed name is
 * body-free only through a visible construction, an untyped one is followed to what it holds.
 */
function declaredReason(
  id: ts.Identifier,
  bareReason: string,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
  seen: Set<Declaration>,
): string | undefined {
  for (const decl of resolveDeclarations(typed, id) ?? []) {
    let reason: string | undefined;
    if (!decl.typed) {
      reason = untypedReason(decl, id.text, typed, constructors, seen);
    } else if (decl.reassigned) reason = reassignedReason;
    else if (decl.init === undefined) reason = bareReason;
    else if (seen.has(decl)) reason = 'Request inherits a body';
    else {
      seen.add(decl);
      const construction = unwrap(decl.init);
      reason = isRequestConstruction(construction, constructors)
        ? requestBodyReason(construction, typed, constructors, seen)
        : unprovenReason;
    }
    if (reason !== undefined) return reason;
  }
  return undefined;
}

/** Whether syntax says what the value is: a literal holds no `Request`. */
function isInertValue(value: ts.Expression): boolean {
  return (
    ts.isLiteralExpression(value) ||
    ts.isTemplateExpression(value) ||
    ts.isArrayLiteralExpression(value) ||
    ts.isObjectLiteralExpression(value) ||
    value.kind === ts.SyntaxKind.TrueKeyword ||
    value.kind === ts.SyntaxKind.FalseKeyword ||
    value.kind === ts.SyntaxKind.NullKeyword
  );
}

/**
 * An untyped declaration is followed to its initializer and every plain assignment. What syntax
 * cannot read (a call result, a member, a destructured or compound write) is reported when the
 * file also types the name `Request`, so a shadow never turns a flagged use into a pass.
 */
function untypedReason(
  decl: Declaration,
  name: string,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
  seen: Set<Declaration>,
): string | undefined {
  const settled = decl.annotated && decl.assigned.length === 0 && !decl.unknownAssign;
  if (settled || seen.has(decl)) return undefined;
  const rebound = (typed.get(name)?.typedOnes.length ?? 0) > 0;
  if (decl.opaque || decl.unknownAssign) return rebound ? opaqueReason : undefined;
  // A caller can pass any value for an untyped parameter, whatever its default.
  if (decl.param && !decl.annotated && rebound) return unknownReason;
  seen.add(decl);
  const values = decl.init === undefined ? decl.assigned : [decl.init, ...decl.assigned];
  if (values.length === 0) return rebound ? unknownReason : undefined;
  for (const value of values) {
    const held = unwrap(value);
    const construction = isRequestConstruction(held, constructors);
    const reason = construction
      ? requestBodyReason(held, typed, constructors, seen)
      : inheritedBodyReason(held, typed, constructors, seen);
    if (reason !== undefined) return reason;
    if (rebound && !construction && !knownOther(held, typed, new Set())) return unknownReason;
  }
  return undefined;
}

/** Whether syntax proves a value is not a `Request` that inheritedBodyReason missed. */
function knownOther(
  value: ts.Expression,
  typed: RequestTyped,
  visiting: Set<Declaration>,
): boolean {
  const v = unwrap(value);
  if (isInertValue(v)) return true;
  if (ts.isConditionalExpression(v)) {
    return knownOther(v.whenTrue, typed, visiting) && knownOther(v.whenFalse, typed, visiting);
  }
  if (ts.isBinaryExpression(v)) {
    const kind = v.operatorToken.kind;
    // An assignment evaluates to the assigned value, which may be a `Request`.
    if (kind === ts.SyntaxKind.EqualsToken) return knownOther(v.right, typed, visiting);
    if (kind >= ts.SyntaxKind.FirstAssignment && kind <= ts.SyntaxKind.LastAssignment) return false;
    return knownOther(v.left, typed, visiting) && knownOther(v.right, typed, visiting);
  }
  if (!ts.isIdentifier(v)) return false;
  const decls = resolveDeclarations(typed, v);
  return (
    decls !== undefined &&
    decls.every((d) => {
      if (d.typed || (d.param && !d.annotated)) return false;
      if (d.opaque || d.unknownAssign || visiting.has(d)) return false;
      if (d.annotated && d.assigned.length === 0) return true;
      visiting.add(d);
      const values = d.init === undefined ? d.assigned : [d.init, ...d.assigned];
      return values.length > 0 && values.every((x) => knownOther(x, typed, visiting));
    })
  );
}

/**
 * Why the input of a `new Request(input, init)` may already carry a body, or undefined: an
 * identifier, either side of a conditional or `??`/`||`/`&&`, and `x.clone()` are followed or
 * reported; any other expression (a member read, a call) is not (see the header's named limits).
 */
function inheritedBodyReason(
  input: ts.Expression,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
  seen: Set<Declaration>,
): string | undefined {
  const source = unwrap(input);
  if (ts.isIdentifier(source)) {
    return declaredReason(source, 'Request inherits a body', typed, constructors, seen);
  }
  if (ts.isConditionalExpression(source)) {
    return (
      inheritedBodyReason(source.whenTrue, typed, constructors, new Set(seen)) ??
      inheritedBodyReason(source.whenFalse, typed, constructors, new Set(seen))
    );
  }
  if (
    ts.isBinaryExpression(source) &&
    (source.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken ||
      source.operatorToken.kind === ts.SyntaxKind.BarBarToken ||
      source.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken)
  ) {
    return (
      inheritedBodyReason(source.left, typed, constructors, new Set(seen)) ??
      inheritedBodyReason(source.right, typed, constructors, new Set(seen))
    );
  }
  if (ts.isCallExpression(source) && memberName(unwrap(source.expression)) === 'clone') {
    return 'Request inherits a body (clone)';
  }
  return undefined;
}

/** Why a `Request`-typed identifier may carry a body, or undefined when it is not typed. */
export function typedRequestReason(
  id: ts.Identifier,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
  seen: Set<Declaration> = new Set<Declaration>(),
): string | undefined {
  return declaredReason(id, 'Request-typed first argument', typed, constructors, seen);
}

/**
 * An input that is a bare name no declaration holds a `Request` for: a global or import, or an
 * untyped parameter (a caller can pass any value). Only a direct identifier input is read here.
 */
function unreadInputReason(input: ts.Expression, typed: RequestTyped): string | undefined {
  const source = unwrap(input);
  if (!ts.isIdentifier(source)) return undefined;
  const declarations = resolveDeclarations(typed, source);
  if (declarations === undefined) return 'Request input is not a declared name';
  const untypedParameter = declarations.some((d) => d.param && !d.typed && !d.annotated);
  return untypedParameter ? 'Request input is a parameter with no string type' : undefined;
}

/** Why a `new Request(input, init)` may carry a body, or undefined when `init` proves it cannot. */
export function requestBodyReason(
  node: ts.NewExpression,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
  seen: Set<Declaration> = new Set<Declaration>(),
): string | undefined {
  const input = node.arguments?.[0];
  if (input !== undefined) {
    const unread = unreadInputReason(input, typed);
    if (unread !== undefined) return unread;
    const inherited = inheritedBodyReason(input, typed, constructors, seen);
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
