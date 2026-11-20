/*
 * The shared state of one scan of a source file (requestCapScan.testing.ts): what the file
 * declares up front (fetch aliases, `Request` names, SDK imports) and where findings go.
 */
import ts from 'typescript';

import { fetchAliases, type FetchRefs } from './requestCapScan.aliases.testing.js';
import { type RequestTyped, requestTypedNames } from './requestCapScan.declarations.testing.js';
import { requestConstructorAliases } from './requestCapScan.requests.testing.js';

export type Finding = { file: string; function: string; kind: string; detail: string };

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

export type SdkImports = {
  locals: ReadonlyMap<string, string>;
  namespaces: ReadonlySet<string>;
};

/** SDK operations imported under another name, and the names of namespace imports. */
function sdkImports(sf: ts.SourceFile, routes: ReadonlyMap<string, string>): SdkImports {
  const locals = new Map<string, string>();
  const namespaces = new Set<string>();
  for (const node of sf.statements) {
    const bindings = ts.isImportDeclaration(node) ? node.importClause?.namedBindings : undefined;
    if (bindings === undefined) continue;
    if (ts.isNamespaceImport(bindings)) namespaces.add(bindings.name.text);
    else {
      for (const spec of bindings.elements) {
        const op = (spec.propertyName ?? spec.name).text;
        if (routes.has(op)) locals.set(spec.name.text, op);
      }
    }
  }
  return { locals, namespaces };
}

export type ScanContext = {
  file: string;
  routes: ReadonlyMap<string, string>;
  caps: Readonly<Record<string, number>>;
  allow: Readonly<Record<string, string>>;
  constants: ReadonlyMap<string, string>;
  refs: FetchRefs;
  requestTyped: RequestTyped;
  constructors: ReadonlySet<string>;
  sdk: SdkImports;
  findings: Finding[];
  usedRoutes: Set<string>;
  /** `new Request` nodes already judged as the first argument of a fetch. */
  handled: Set<ts.Node>;
};

export function createContext(
  sf: ts.SourceFile,
  routes: ReadonlyMap<string, string>,
  caps: Readonly<Record<string, number>>,
  allow: Readonly<Record<string, string>>,
): ScanContext {
  const aliasSets = fetchAliases(sf);
  return {
    file: sf.fileName,
    routes,
    caps,
    allow,
    constants: stringConstants(sf),
    refs: { names: aliasSets.names, properties: aliasSets.properties },
    requestTyped: requestTypedNames(sf),
    constructors: requestConstructorAliases(sf),
    sdk: sdkImports(sf, routes),
    findings: [],
    usedRoutes: new Set<string>(),
    handled: new Set<ts.Node>(),
  };
}

export function report(ctx: ScanContext, node: ts.Node, kind: string, detail: string): void {
  ctx.findings.push({ file: ctx.file, function: enclosingFunction(node), kind, detail });
}

/** A finding on a send path: exempt when the enclosing function is an allow-list entry. */
export function reportSend(
  ctx: ScanContext,
  node: ts.Node,
  kind: string,
  form: string,
  why: string,
): void {
  const key = `${ctx.file}#${enclosingFunction(node)}`;
  if (ctx.allow[key] === undefined) report(ctx, node, kind, `${key}: ${form} (${why})`);
}

/** The SDK operation a callee names: bare, imported under an alias, or on a namespace import. */
export function sdkOperation(ctx: ScanContext, callee: ts.Expression): string | undefined {
  if (ts.isIdentifier(callee)) {
    const op = ctx.sdk.locals.get(callee.text) ?? callee.text;
    return ctx.routes.has(op) ? op : undefined;
  }
  const onNamespace =
    ts.isPropertyAccessExpression(callee) &&
    ts.isIdentifier(callee.expression) &&
    ctx.sdk.namespaces.has(callee.expression.text);
  return onNamespace && ctx.routes.has(callee.name.text) ? callee.name.text : undefined;
}
