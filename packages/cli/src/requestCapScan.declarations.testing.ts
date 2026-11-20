/*
 * The declarations behind requestCapScan.requests.testing.ts: every declaration of a name in a
 * file, indexed by the scope it binds in, how a use resolves to the nearest scope declaring it,
 * and which names are typed `Request` (directly, through an alias or through a wrapper).
 */
import ts from 'typescript';

import { unwrap } from './requestCapScan.aliases.testing.js';

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
export type RequestTyped = Map<string, NameDeclarations>;

/** The declarations of one name, indexed by scope so a lookup never scans the whole file. */
export type NameDeclarations = {
  byScope: Map<ts.Node, Declaration[]>;
  /** The `Request`-typed ones: what a use no scope reaches is checked against. */
  typedOnes: Declaration[];
};

const requestName = /(^|\.)Request$/;
const requestWrappers: ReadonlySet<string> = new Set([
  'Readonly',
  'Required',
  'Partial',
  'NonNullable',
]);

/**
 * Whether a type node is a `Request`: the name, `ns.Request`, a file alias of one (`type R =
 * Request`, `interface R extends Request`), a union or intersection containing one, or a wrapper
 * (`Readonly<Request>`). Alias names are file-wide, so an unsure name is read as a `Request`.
 */
function isRequestTypeNode(node: ts.TypeNode | undefined, aliases: ReadonlySet<string>): boolean {
  if (node === undefined) return false;
  if (ts.isUnionTypeNode(node) || ts.isIntersectionTypeNode(node)) {
    return node.types.some((type) => isRequestTypeNode(type, aliases));
  }
  if (ts.isParenthesizedTypeNode(node)) return isRequestTypeNode(node.type, aliases);
  if (ts.isTypeReferenceNode(node)) {
    const name = node.typeName.getText();
    if (requestName.test(name) || aliases.has(name)) return true;
    return (
      requestWrappers.has(name) &&
      (node.typeArguments ?? []).some((type) => isRequestTypeNode(type, aliases))
    );
  }
  return requestName.test(node.getText());
}

/** Names of type aliases and interfaces that are a `Request`, to a fixpoint (`type R = Request`). */
function requestTypeAliases(sf: ts.SourceFile): Set<string> {
  const aliases = new Set<string>();
  const isRequestHeritage = (clause: ts.HeritageClause): boolean =>
    clause.types.some((type) => {
      const name = type.expression.getText();
      return requestName.test(name) || aliases.has(name);
    });
  let changed = true;
  const requestAliasName = (node: ts.Node): string | undefined => {
    if (ts.isTypeAliasDeclaration(node)) {
      return isRequestTypeNode(node.type, aliases) ? node.name.text : undefined;
    }
    if (ts.isImportSpecifier(node) && node.propertyName !== undefined) {
      return requestName.test(node.propertyName.text) ? node.name.text : undefined;
    }
    if (ts.isInterfaceDeclaration(node)) {
      return (node.heritageClauses ?? []).some(isRequestHeritage) ? node.name.text : undefined;
    }
    return undefined;
  };
  const walk = (node: ts.Node): void => {
    const name = requestAliasName(node);
    if (name !== undefined && !aliases.has(name)) {
      aliases.add(name);
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
export function resolveDeclarations(
  typed: RequestTyped,
  id: ts.Identifier,
): Declaration[] | undefined {
  const declarations = typed.get(id.text);
  if (declarations === undefined) return undefined;
  const { byScope, typedOnes } = declarations;
  let child: ts.Node = id;
  for (let scope: ts.Node | undefined = id.parent; scope; child = scope, scope = scope.parent) {
    // A parameter initializer cannot see the body's `var`s of its own function.
    const inParameters = ts.isFunctionLike(scope) && ts.isParameter(child);
    const inScope = byScope.get(scope);
    if (inScope === undefined) continue;
    const here = inParameters ? inScope.filter((d) => d.param) : inScope;
    if (here.length > 0) return here;
  }
  // No declaration in reach (a global, a name bound where the walk cannot see): fail closed on
  // every `Request`-typed declaration of the name rather than pass it.
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

/** Assignment operators that overwrite a typed name, so its initializer proves nothing. */
const assignmentOperators: ReadonlySet<ts.SyntaxKind> = new Set([
  ts.SyntaxKind.EqualsToken,
  ts.SyntaxKind.QuestionQuestionEqualsToken,
  ts.SyntaxKind.BarBarEqualsToken,
  ts.SyntaxKind.AmpersandAmpersandEqualsToken,
]);

const freshDeclaration = (scope: ts.Node): Declaration => ({
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

/** Every parameter, variable and function declaration of the file, indexed by name and scope. */
function collectDeclarations(sf: ts.SourceFile): RequestTyped {
  const typed: RequestTyped = new Map();
  const typeAliases = requestTypeAliases(sf);
  const declare = (id: ts.Identifier, declaration: Declaration): void => {
    let names = typed.get(id.text);
    if (names === undefined) {
      names = { byScope: new Map(), typedOnes: [] };
      typed.set(id.text, names);
    }
    const inScope = names.byScope.get(declaration.scope);
    if (inScope === undefined) names.byScope.set(declaration.scope, [declaration]);
    else inScope.push(declaration);
    if (declaration.typed) names.typedOnes.push(declaration);
  };
  const collect = (node: ts.Node): void => {
    if (ts.isParameter(node) || ts.isVariableDeclaration(node)) {
      const scope = declarationScope(node);
      if (ts.isIdentifier(node.name)) {
        declare(node.name, {
          ...freshDeclaration(scope),
          typed: isRequestTypeNode(node.type, typeAliases),
          init: node.initializer,
          param: ts.isParameter(node),
          annotated: isKeywordType(node.type),
        });
      } else {
        for (const id of bindingIdentifiers(node.name)) {
          declare(id, { ...freshDeclaration(scope), param: ts.isParameter(node), opaque: true });
        }
      }
    } else if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      declare(node.name, { ...freshDeclaration(node.parent), annotated: true });
    }
    ts.forEachChild(node, collect);
  };
  collect(sf);
  return typed;
}

/** Records on each declaration the writes that overwrite it after its initializer. */
function findAssignments(node: ts.Node, typed: RequestTyped): void {
  if (ts.isBinaryExpression(node) && assignmentOperators.has(node.operatorToken.kind)) {
    const plain = node.operatorToken.kind === ts.SyntaxKind.EqualsToken;
    markAssigned(node.left, typed, plain || ts.isIdentifier(node.left) ? node.right : undefined);
  } else if (
    (ts.isForOfStatement(node) || ts.isForInStatement(node)) &&
    !ts.isVariableDeclarationList(node.initializer)
  ) {
    markAssigned(node.initializer, typed);
  }
  ts.forEachChild(node, (child) => findAssignments(child, typed));
}

export function requestTypedNames(sf: ts.SourceFile): RequestTyped {
  const typed = collectDeclarations(sf);
  findAssignments(sf, typed);
  return typed;
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
