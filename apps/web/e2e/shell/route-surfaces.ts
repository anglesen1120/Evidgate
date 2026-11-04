// The app's route table, read as source. A CSP walk that spells its addresses by hand drifts the
// moment an entry, a tab or a review route is added, so the walk derives them from the table the
// router really mounts: `app.routes.ts`, the navigation registry it is generated from, the lazy
// tables each entry loads and the fixture-only review routes this suite's build serves.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import ts from 'typescript';

import { isPhaseActive } from '../../src/app/shell/nav/product-phase';
import type { ProductPhase } from '../../src/app/shell/nav/nav.models';

/** One address the router can mount: the path its table declares and the component it renders. */
export interface Surface {
  readonly path: string;
  readonly component: string;
  /** Every `permissionGuard` code on the way down, so a caller can pick a session for it. */
  readonly permissions: readonly string[];
}

const APP = resolve(__dirname, '../../src/app');
const APP_ROUTES = resolve(APP, 'app.routes.ts');
/** This suite serves the fixture route table, so its review addresses are real surfaces. */
const DEV_ROUTES = resolve(APP, 'core/dev-routes/dev-routes.fixtures.ts');

const sources = new Map<string, ts.SourceFile>();

function source(file: string): ts.SourceFile {
  const parsed = sources.get(file);
  if (parsed !== undefined) return parsed;
  const created = ts.createSourceFile(
    file,
    readFileSync(file, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  );
  sources.set(file, created);
  return created;
}

function property(node: ts.Node, name: string): ts.Expression | undefined {
  if (!ts.isObjectLiteralExpression(node)) return undefined;
  return node.properties.find(
    (member): member is ts.PropertyAssignment =>
      ts.isPropertyAssignment(member) && member.name.getText() === name,
  )?.initializer;
}

/** A literal's value, or the source text of a reference (`MembersPage`, `devRoutes()`). */
function text(node: ts.Node | undefined): string {
  if (node === undefined) return '';
  return ts.isStringLiteralLike(node) ? node.text : node.getText();
}

function declaration(file: string, name: string): ts.Expression {
  for (const statement of source(file).statements) {
    if (!ts.isVariableStatement(statement)) continue;
    for (const variable of statement.declarationList.declarations) {
      if (variable.name.getText() === name && variable.initializer !== undefined) {
        return variable.initializer;
      }
    }
  }
  throw new Error(`The route walk cannot resolve ${name} in ${file}`);
}

function objects(node: ts.Node): ts.ObjectLiteralExpression[] {
  const found: ts.ObjectLiteralExpression[] = [];
  const visit = (child: ts.Node): void => {
    if (ts.isObjectLiteralExpression(child)) found.push(child);
    else ts.forEachChild(child, visit);
  };
  visit(node);
  return found;
}

/** The module a lazy loader imports, resolved next to the file that spells the loader. */
function importedModule(node: ts.Node, file: string): string {
  let specifier: string | undefined;
  const visit = (child: ts.Node): void => {
    if (ts.isCallExpression(child) && child.expression.kind === ts.SyntaxKind.ImportKeyword) {
      specifier = text(child.arguments[0]);
    }
    ts.forEachChild(child, visit);
  };
  visit(node);
  if (specifier === undefined) throw new Error(`A lazy route in ${file} names no module`);
  return resolve(dirname(file), `${specifier}.ts`);
}

/** The exported class a `loadComponent` arrow returns (`(m) => m.RunListPage`). */
function lazyComponent(node: ts.Node): string {
  let component = '';
  const visit = (child: ts.Node): void => {
    if (ts.isPropertyAccessExpression(child) && /^[A-Z]/.test(child.name.text)) {
      component = child.name.text;
    }
    ts.forEachChild(child, visit);
  };
  visit(node);
  if (component === '') throw new Error(`A component loader names no class: ${node.getText()}`);
  return component;
}

/** The phases the build reaches; a later phase's route never matches, so it is not a surface. */
function active(node: ts.Node): boolean {
  const phase = text(property(node, 'phase'));
  if (phase !== 'G1' && phase !== 'G2' && phase !== 'G3' && phase !== 'G4') {
    throw new Error(`Unrecognized route phase: ${phase}`);
  }
  return isPhaseActive(phase satisfies ProductPhase);
}

/** The literal codes of every `permissionGuard('code')` on one route. */
function requiredPermissions(node: ts.Node): string[] {
  const codes: string[] = [];
  for (const key of ['canMatch', 'canActivate']) {
    const guards = property(node, key);
    if (guards === undefined) continue;
    const visit = (child: ts.Node): void => {
      if (ts.isCallExpression(child) && child.expression.getText() === 'permissionGuard') {
        const [code] = child.arguments;
        if (code !== undefined && ts.isStringLiteralLike(code)) codes.push(code.text);
      }
      ts.forEachChild(child, visit);
    };
    visit(guards);
  }
  return codes;
}

/** The routes of one registry scope, expanded from the registry the table is built on. */
function entrySurfaces(scope: string, prefix: string, inherited: readonly string[]): Surface[] {
  const registry = resolve(APP, 'shell/nav/nav-registry.ts');
  return objects(declaration(registry, 'NAV_ENTRIES'))
    .filter((entry) => text(property(entry, 'scope')) === scope && active(entry))
    .flatMap((entry) => {
      const id = text(property(entry, 'id'));
      const path = `${prefix}/${text(property(entry, 'segment'))}`;
      const permissions = [...inherited, text(property(entry, 'permission'))];
      const page = property(declaration(APP_ROUTES, 'ENTRY_PAGES'), id);
      if (page !== undefined) return [{ path, component: lazyComponent(page), permissions }];
      const loader =
        property(declaration(APP_ROUTES, 'ENTRY_ROUTES'), id) ??
        property(declaration(APP_ROUTES, 'AREA_ROUTES'), text(property(entry, 'area')));
      if (loader === undefined) throw new Error(`Navigation entry ${id} loads no route table`);
      const childFile = importedModule(loader, APP_ROUTES);
      return routeSurfaces(declaration(childFile, 'routes'), childFile, path, permissions);
    });
}

/** One child route per Settings tab the build reaches, with the body the tab mounts. */
function tabSurfaces(prefix: string, inherited: readonly string[]): Surface[] {
  const tabs = resolve(APP, 'features/settings/settings-tabs.ts');
  const shell = resolve(APP, 'features/settings/settings-shell.routes.ts');
  const bodies = declaration(shell, 'TAB_BODIES');
  return objects(declaration(tabs, 'SETTINGS_TABS'))
    .filter(active)
    .map((tab) => {
      const id = text(property(tab, 'id'));
      return {
        path: `${prefix}/${text(property(tab, 'segment'))}`,
        component: text(property(bodies, id)) || 'SettingsTabPlaceholderPage',
        permissions: [...inherited, text(property(tab, 'permission'))],
      };
    });
}

function spreadSurfaces(
  node: ts.Expression,
  file: string,
  prefix: string,
  inherited: readonly string[],
): Surface[] {
  const name = node.getText();
  if (name === 'devRoutes()') {
    const factory = source(DEV_ROUTES).statements.find(
      (statement): statement is ts.FunctionDeclaration =>
        ts.isFunctionDeclaration(statement) && statement.name?.text === 'devRoutes',
    );
    const table = factory?.body?.statements.find(ts.isReturnStatement)?.expression;
    if (table === undefined) throw new Error('The fixture route table returns nothing');
    return routeSurfaces(table, DEV_ROUTES, prefix, inherited);
  }
  if (name === 'AUTH_ROUTES') {
    const authFile = resolve(APP, 'features/auth/auth.routes.ts');
    return routeSurfaces(declaration(authFile, name), authFile, prefix, inherited);
  }
  if (name === 'ORG_SCOPE_ENTRY_ROUTES') return entrySurfaces('org', prefix, inherited);
  if (name === 'PROJECT_SCOPE_ENTRY_ROUTES') return entrySurfaces('project', prefix, inherited);
  if (name === 'TAB_ROUTES') return tabSurfaces(prefix, inherited);
  throw new Error(`Unsupported route spread ${name} in ${file}: extend the route walk`);
}

function routeSurfaces(
  node: ts.Node,
  file: string,
  prefix = '',
  inherited: readonly string[] = [],
): Surface[] {
  if (ts.isArrayLiteralExpression(node)) {
    return node.elements.flatMap((child) =>
      ts.isSpreadElement(child)
        ? spreadSurfaces(child.expression, file, prefix, inherited)
        : routeSurfaces(child, file, prefix, inherited),
    );
  }
  if (!ts.isObjectLiteralExpression(node)) throw new Error(`Unsupported route table in ${file}`);
  if (property(node, 'redirectTo') !== undefined) return [];
  const segment = property(node, 'path');
  if (segment === undefined || !ts.isStringLiteralLike(segment)) {
    throw new Error(`A route path in ${file} is not a literal: extend the route walk`);
  }
  const name = segment.text === '**' ? 'csp-unmatched' : segment.text;
  const path = `${prefix}/${name}`.replace(/\/+$/, '');
  const permissions = [...inherited, ...requiredPermissions(node)];
  const children = property(node, 'children');
  if (children !== undefined) return routeSurfaces(children, file, path, permissions);
  const loader = property(node, 'loadChildren');
  if (loader !== undefined) {
    const childFile = importedModule(loader, file);
    return routeSurfaces(declaration(childFile, 'routes'), childFile, path, permissions);
  }
  const component = property(node, 'component');
  const lazy = property(node, 'loadComponent');
  if (component === undefined && lazy === undefined) return [];
  const mounted = lazy === undefined ? text(component) : lazyComponent(lazy);
  return [{ path, component: mounted, permissions }];
}

/** Every address the fixtures build mounts, in table order. */
export function surfacesInRouteTable(): readonly Surface[] {
  return routeSurfaces(declaration(APP_ROUTES, 'routes'), APP_ROUTES);
}

/** One route's path with its parameters filled, and a name for any parameter left unnamed. */
export function fillParams(path: string, ids: Readonly<Record<string, string>>): string {
  return path.replace(/:(\w+)/g, (_, name: string) => {
    const value = ids[name];
    if (value === undefined) throw new Error(`The route walk has no id for :${name}`);
    return value;
  });
}
