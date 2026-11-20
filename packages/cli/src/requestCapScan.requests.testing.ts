/*
 * The `Request` analysis behind requestCapScan.testing.ts: which names are typed `Request`, how a
 * use resolves to its declarations by lexical scope, and why a `Request` input may carry a body.
 */
import ts from 'typescript';

import { memberName, propertyNamed, unwrap } from './requestCapScan.aliases.testing.js';

/**
 * One declaration of a name: where it binds, whether it is typed `Request`, its initializer, and
 * whether a later assignment overwrites it (so the initializer then proves nothing).
 */
export type Declaration = {
  scope: ts.Node;
  typed: boolean;
  init: ts.Expression | undefined;
  reassigned: boolean;
  /** Values later assigned with a plain `=`; they can carry a `Request` as much as the initializer. */
  assigned: ts.Expression[];
  /** Written by a form whose value syntax cannot read (destructuring, `for...of`, compound). */
  unknownAssign: boolean;
  /** A parameter: visible to the parameter initializers of its function, unlike a body `var`. */
  param: boolean;
  /** Carries a non-`Request` type annotation (or is a function): it is known not to be one. */
  annotated: boolean;
  /** A destructured binding: what it holds is unknown. */
  opaque: boolean;
};

/** Every declaration in the file, by name; a use resolves to the nearest scope declaring it. */
export type RequestTyped = Map<string, Declaration[]>;

/** Whether a type node names `Request`: `Request`, `ns.Request`, or a union containing one. */
function isRequestTypeNode(node: ts.TypeNode | undefined): boolean {
  if (node === undefined) return false;
  if (ts.isUnionTypeNode(node)) return node.types.some(isRequestTypeNode);
  if (ts.isParenthesizedTypeNode(node)) return isRequestTypeNode(node.type);
  return /(^|\.)Request$/.test(node.getText());
}

/** The scope a declaration binds in: `var` hoists to its function, `let`/`const` to its block. */
function declarationScope(node: ts.VariableDeclaration | ts.ParameterDeclaration): ts.Node {
  if (ts.isParameter(node)) return node.parent;
  const list = node.parent;
  if (ts.isCatchClause(list)) return list;
  const blockScoped = (list.flags & ts.NodeFlags.BlockScoped) !== 0;
  const statement = list.parent;
  if (!blockScoped) {
    let current: ts.Node = statement;
    while (!ts.isFunctionLike(current) && !ts.isSourceFile(current) && !ts.isModuleBlock(current)) {
      current = current.parent;
    }
    return current;
  }
  const isLoopHead =
    (ts.isForStatement(statement) ||
      ts.isForInStatement(statement) ||
      ts.isForOfStatement(statement)) &&
    statement.initializer === list;
  return isLoopHead ? statement : statement.parent;
}

function bindingIdentifiers(name: ts.BindingName): ts.Identifier[] {
  if (ts.isIdentifier(name)) return [name];
  return name.elements.flatMap((element) =>
    ts.isOmittedExpression(element) ? [] : bindingIdentifiers(element.name),
  );
}

/** The declarations the lexically nearest scope declaring the identifier's name holds. */
function resolveDeclarations(typed: RequestTyped, id: ts.Identifier): Declaration[] | undefined {
  const declarations = typed.get(id.text);
  if (declarations === undefined) return undefined;
  let child: ts.Node = id;
  for (let scope: ts.Node | undefined = id.parent; scope; child = scope, scope = scope.parent) {
    // A parameter initializer cannot see the body's `var`s of its own function.
    const inParameters = ts.isFunctionLike(scope) && ts.isParameter(child);
    const here = declarations.filter((d) => d.scope === scope && (!inParameters || d.param));
    if (here.length > 0) return here;
  }
  // No declaration in reach (a global, a name bound where the walk cannot see): fail closed on
  // every `Request`-typed declaration of the name rather than pass it.
  const typedOnes = declarations.filter((d) => d.typed);
  return typedOnes.length > 0 ? typedOnes : undefined;
}

/** Marks the names an assignment target overwrites, including destructuring targets. */
function markAssigned(node: ts.Node, typed: RequestTyped, value?: ts.Expression): void {
  const target = ts.isExpression(node) ? unwrap(node) : node;
  if (ts.isIdentifier(target)) {
    for (const decl of resolveDeclarations(typed, target) ?? []) {
      decl.reassigned = true;
      if (value === undefined) decl.unknownAssign = true;
      else decl.assigned.push(value);
    }
    return;
  }
  if (ts.isArrayLiteralExpression(target)) {
    for (const element of target.elements) markAssigned(element, typed);
  } else if (ts.isObjectLiteralExpression(target)) {
    for (const property of target.properties) {
      if (ts.isShorthandPropertyAssignment(property)) markAssigned(property.name, typed);
      else if (ts.isPropertyAssignment(property)) markAssigned(property.initializer, typed);
      else if (ts.isSpreadAssignment(property)) markAssigned(property.expression, typed);
    }
  } else if (ts.isSpreadElement(target)) markAssigned(target.expression, typed);
  else if (ts.isBinaryExpression(target)) markAssigned(target.left, typed);
}

export function requestTypedNames(sf: ts.SourceFile): RequestTyped {
  const typed: RequestTyped = new Map();
  const declare = (id: ts.Identifier, declaration: Declaration): void => {
    const list = typed.get(id.text);
    if (list === undefined) typed.set(id.text, [declaration]);
    else list.push(declaration);
  };
  const fresh = (scope: ts.Node): Declaration => ({
    scope,
    typed: false,
    init: undefined,
    reassigned: false,
    assigned: [],
    unknownAssign: false,
    param: false,
    annotated: false,
    opaque: false,
  });
  const collect = (node: ts.Node): void => {
    if (ts.isParameter(node) || ts.isVariableDeclaration(node)) {
      const scope = declarationScope(node);
      if (ts.isIdentifier(node.name)) {
        const isTyped = isRequestTypeNode(node.type);
        declare(node.name, {
          ...fresh(scope),
          typed: isTyped,
          init: node.initializer,
          param: ts.isParameter(node),
          annotated: isKeywordType(node.type),
        });
      } else {
        for (const id of bindingIdentifiers(node.name)) {
          declare(id, { ...fresh(scope), param: ts.isParameter(node), opaque: true });
        }
      }
    } else if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      declare(node.name, { ...fresh(node.parent), annotated: true });
    }
    ts.forEachChild(node, collect);
  };
  collect(sf);
  const findAssignments = (node: ts.Node): void => {
    if (ts.isBinaryExpression(node) && assignmentOperators.has(node.operatorToken.kind)) {
      const plain = node.operatorToken.kind === ts.SyntaxKind.EqualsToken;
      markAssigned(node.left, typed, plain || ts.isIdentifier(node.left) ? node.right : undefined);
    } else if (
      (ts.isForOfStatement(node) || ts.isForInStatement(node)) &&
      !ts.isVariableDeclarationList(node.initializer)
    ) {
      markAssigned(node.initializer, typed);
    }
    ts.forEachChild(node, findAssignments);
  };
  findAssignments(sf);
  return typed;
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

/** Assignment operators that overwrite a typed name, so its initializer proves nothing. */
const assignmentOperators: ReadonlySet<ts.SyntaxKind> = new Set([
  ts.SyntaxKind.EqualsToken,
  ts.SyntaxKind.QuestionQuestionEqualsToken,
  ts.SyntaxKind.BarBarEqualsToken,
  ts.SyntaxKind.AmpersandAmpersandEqualsToken,
]);

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
  const rebound = typed.get(name)?.some((d) => d.typed) === true;
  if (decl.opaque || decl.unknownAssign) return rebound ? opaqueReason : undefined;
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

/** Whether a type annotation is a primitive keyword (`string`, `number`, ...), never a `Request`. */
function isKeywordType(type: ts.TypeNode | undefined): boolean {
  if (type === undefined) return false;
  if (ts.isArrayTypeNode(type)) return isKeywordType(type.elementType);
  return (
    ts.isLiteralTypeNode(type) ||
    type.kind === ts.SyntaxKind.StringKeyword ||
    type.kind === ts.SyntaxKind.NumberKeyword ||
    type.kind === ts.SyntaxKind.BooleanKeyword ||
    type.kind === ts.SyntaxKind.BigIntKeyword ||
    type.kind === ts.SyntaxKind.VoidKeyword ||
    type.kind === ts.SyntaxKind.UndefinedKeyword
  );
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
    return knownOther(v.left, typed, visiting) && knownOther(v.right, typed, visiting);
  }
  if (!ts.isIdentifier(v)) return false;
  const decls = resolveDeclarations(typed, v);
  return (
    decls !== undefined &&
    decls.every((d) => {
      if (d.typed) return true;
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

/** Why a `new Request(input, init)` may carry a body, or undefined when `init` proves it cannot. */
export function requestBodyReason(
  node: ts.NewExpression,
  typed: RequestTyped,
  constructors: ReadonlySet<string>,
  seen: Set<Declaration> = new Set<Declaration>(),
): string | undefined {
  const input = node.arguments?.[0];
  if (input !== undefined) {
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
