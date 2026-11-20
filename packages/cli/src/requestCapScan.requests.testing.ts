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
  /** A destructured binding: what it holds is unknown, so it never proves a name is not a `Request`. */
  opaque?: boolean;
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
  for (let scope: ts.Node | undefined = id.parent; scope; scope = scope.parent) {
    const here = declarations.filter((d) => d.scope === scope);
    if (here.length > 0) return here;
  }
  // No declaration in reach (a global, a name bound where the walk cannot see): fail closed on
  // every `Request`-typed declaration of the name rather than pass it.
  const typedOnes = declarations.filter((d) => d.typed);
  return typedOnes.length > 0 ? typedOnes : undefined;
}

/** Marks the names an assignment target overwrites, including destructuring targets. */
function markAssigned(target: ts.Node, typed: RequestTyped): void {
  if (ts.isIdentifier(target)) {
    for (const decl of resolveDeclarations(typed, target) ?? []) decl.reassigned = true;
    return;
  }
  if (ts.isParenthesizedExpression(target)) return markAssigned(target.expression, typed);
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
    typed.set(id.text, [...(typed.get(id.text) ?? []), declaration]);
  };
  const collect = (node: ts.Node): void => {
    if (ts.isParameter(node) || ts.isVariableDeclaration(node)) {
      const scope = declarationScope(node);
      if (ts.isIdentifier(node.name)) {
        const isTyped = isRequestTypeNode(node.type);
        declare(node.name, {
          scope,
          typed: isTyped,
          init: node.initializer,
          reassigned: false,
        });
      } else {
        for (const id of bindingIdentifiers(node.name)) {
          declare(id, { scope, typed: false, init: undefined, reassigned: false, opaque: true });
        }
      }
    } else if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      declare(node.name, { scope: node.parent, typed: false, init: undefined, reassigned: false });
    }
    ts.forEachChild(node, collect);
  };
  collect(sf);
  const findAssignments = (node: ts.Node): void => {
    if (ts.isBinaryExpression(node) && assignmentOperators.has(node.operatorToken.kind)) {
      markAssigned(node.left, typed);
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
const unprovenReason = 'Request-typed value without a visible Request construction';

/**
 * Why a name used as a `Request` input may carry a body, or undefined when it is not a `Request`
 * or its own `new Request` init proves it cannot. The use resolves to the declarations of the
 * nearest scope that declares the name, and every one of them is checked. A typed name is
 * body-free only through a visible construction; any other initializer, and a later assignment,
 * is reported. An untyped alias is followed to what it holds. A declaration already being
 * followed is circular, so it is reported rather than followed into a loop.
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
    if (decl.typed) {
      if (decl.reassigned) reason = reassignedReason;
      else if (decl.init === undefined) reason = bareReason;
      else if (seen.has(decl)) reason = 'Request inherits a body';
      else {
        seen.add(decl);
        const construction = unwrap(decl.init);
        reason = isRequestConstruction(construction, constructors)
          ? requestBodyReason(construction, typed, constructors, seen)
          : unprovenReason;
      }
    } else if (decl.opaque === true) {
      // A destructured name hides what it holds: reported when the file also types the name `Request`.
      if (typed.get(id.text)?.some((d) => d.typed) === true) reason = opaqueReason;
    } else if (decl.init !== undefined && !decl.reassigned && !seen.has(decl)) {
      seen.add(decl);
      const held = unwrap(decl.init);
      reason = isRequestConstruction(held, constructors)
        ? requestBodyReason(held, typed, constructors, seen)
        : inheritedBodyReason(held, typed, constructors, seen);
    }
    if (reason !== undefined) return reason;
  }
  return undefined;
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
      inheritedBodyReason(source.whenTrue, typed, constructors, seen) ??
      inheritedBodyReason(source.whenFalse, typed, constructors, seen)
    );
  }
  if (
    ts.isBinaryExpression(source) &&
    (source.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken ||
      source.operatorToken.kind === ts.SyntaxKind.BarBarToken ||
      source.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken)
  ) {
    return (
      inheritedBodyReason(source.left, typed, constructors, seen) ??
      inheritedBodyReason(source.right, typed, constructors, seen)
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
