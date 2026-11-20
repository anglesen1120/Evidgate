/*
 * The per-node checks of the request-cap scan (requestCapScan.testing.ts): other transports, the
 * `Request` constructor, and the SDK / client routes that send a body.
 */
import ts from 'typescript';

import { fetchKind, memberName, propertyNamed, unwrap } from './requestCapScan.aliases.testing.js';
import {
  report,
  reportSend,
  type ScanContext,
  sdkOperation,
} from './requestCapScan.context.testing.js';
import { isRequestConstruction, requestBodyReason } from './requestCapScan.requests.testing.js';
import { callsWithFetchAsThis, reflectApplyTarget } from './requestCapScan.send.testing.js';

const verbs: Readonly<Record<string, true>> = { post: true, put: true, patch: true, delete: true };
const otherTransportModules =
  /^(node:)?(https?|http2|net|tls|dgram|undici|node-fetch|axios|got|ws)(\/.*)?$/;
const otherTransportGlobals: Readonly<Record<string, true>> = {
  XMLHttpRequest: true,
  sendBeacon: true,
};

/** `import 'axios'`, `export * from 'axios'`, `import x = require('axios')`. */
export function checkTransportModule(ctx: ScanContext, node: ts.Node): void {
  if (ts.isImportDeclaration(node) && ts.isStringLiteralLike(node.moduleSpecifier)) {
    const name = node.moduleSpecifier.text;
    if (otherTransportModules.test(name)) {
      reportSend(ctx, node, 'other-transport', `import ${name}`, 'module');
    }
  }
  if (
    ts.isExportDeclaration(node) &&
    node.moduleSpecifier !== undefined &&
    ts.isStringLiteralLike(node.moduleSpecifier) &&
    otherTransportModules.test(node.moduleSpecifier.text)
  ) {
    const name = node.moduleSpecifier.text;
    reportSend(ctx, node, 'other-transport', `export ${name}`, 'module');
  }
  if (
    ts.isImportEqualsDeclaration(node) &&
    ts.isExternalModuleReference(node.moduleReference) &&
    ts.isStringLiteralLike(node.moduleReference.expression) &&
    otherTransportModules.test(node.moduleReference.expression.text)
  ) {
    const name = node.moduleReference.expression.text;
    reportSend(ctx, node, 'other-transport', `import ${name}`, 'module');
  }
}

/** `XMLHttpRequest`, `navigator['sendBeacon']`. */
export function checkTransportGlobal(ctx: ScanContext, node: ts.Node): void {
  if (ts.isIdentifier(node) && Object.hasOwn(otherTransportGlobals, node.text)) {
    reportSend(ctx, node, 'other-transport', node.text, 'global');
  }
  const member = ts.isElementAccessExpression(node) ? memberName(node) : undefined;
  if (member !== undefined && Object.hasOwn(otherTransportGlobals, member)) {
    reportSend(ctx, node, 'other-transport', member, 'global');
  }
}

/** A `new Request` not already judged as the first argument of a fetch. */
export function checkRequestConstruction(ctx: ScanContext, node: ts.Node): void {
  if (!isRequestConstruction(node, ctx.constructors) || ctx.handled.has(node)) return;
  const why = requestBodyReason(node, ctx.requestTyped, ctx.constructors);
  if (why !== undefined) reportSend(ctx, node, 'uncapped-request', 'new Request', why);
}

/** `` fetch`...` `` is a tagged template the scan cannot read. */
export function checkTaggedFetch(ctx: ScanContext, node: ts.Node): void {
  if (ts.isTaggedTemplateExpression(node) && fetchKind(node.tag, ctx.refs) !== undefined) {
    reportSend(ctx, node, 'unresolved-fetch', node.tag.getText().slice(0, 60), 'tagged template');
  }
}

/** `require('axios')` and `import('axios')`. */
function checkCallModule(ctx: ScanContext, node: ts.CallExpression): void {
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
      ctx,
      node,
      'other-transport',
      `${isRequire ? 'require' : 'import()'} ${stringArg}`,
      'module',
    );
  }
}

/** A fetch bound with arguments, handed to `call`, or to `Reflect.apply`. */
function checkIndirectFetch(ctx: ScanContext, node: ts.CallExpression): void {
  const arg = node.arguments[0];
  const boundCallee = unwrap(node.expression);
  const boundTarget =
    ts.isPropertyAccessExpression(boundCallee) || ts.isElementAccessExpression(boundCallee)
      ? boundCallee.expression
      : undefined;
  if (
    memberName(boundCallee) === 'bind' &&
    boundTarget !== undefined &&
    fetchKind(boundTarget, ctx.refs) !== undefined &&
    node.arguments.length > 1
  ) {
    reportSend(ctx, node, 'unresolved-fetch', `${boundTarget.getText()}.bind`, 'bound arguments');
  }
  if (arg !== undefined && callsWithFetchAsThis(boundCallee, boundTarget, arg, ctx)) {
    reportSend(
      ctx,
      node,
      'unresolved-fetch',
      boundCallee.getText().slice(0, 60),
      'fetch handed to call',
    );
  }
  if (reflectApplyTarget(ctx, node) !== undefined) {
    reportSend(ctx, node, 'unresolved-fetch', 'Reflect.apply', 'target is a fetch');
  }
}

/** The route a `client.post({ url, body })` call names, or undefined with a report. */
function clientRoute(
  ctx: ScanContext,
  node: ts.CallExpression,
  callee: ts.PropertyAccessExpression,
  object: ts.ObjectLiteralExpression | undefined,
): { route: string | undefined; named: boolean } {
  if (object === undefined && /client/i.test(callee.expression.getText())) {
    report(ctx, node, 'unprovable-args', `${callee.getText()} without options`);
  }
  const url = object === undefined ? undefined : propertyNamed(object, 'url');
  if (object === undefined || url === undefined || !ts.isPropertyAssignment(url)) {
    return { route: undefined, named: false };
  }
  const value = ts.isStringLiteralLike(url.initializer)
    ? url.initializer.text
    : ts.isIdentifier(url.initializer)
      ? ctx.constants.get(url.initializer.text)
      : undefined;
  const route = value === undefined ? undefined : `${callee.name.text.toUpperCase()} ${value}`;
  if (route === undefined) report(ctx, node, 'unresolved-url', callee.getText());
  return { route, named: true };
}

/** An SDK operation or a client verb call that sends a body must be a row of the cap table. */
function checkRoute(ctx: ScanContext, node: ts.CallExpression): void {
  const callee = node.expression;
  const arg = node.arguments[0];
  const plain = arg === undefined ? undefined : unwrap(arg);
  const object = plain !== undefined && ts.isObjectLiteralExpression(plain) ? plain : undefined;
  let route: string | undefined;
  let named = false;
  const operation = sdkOperation(ctx, unwrap(callee));
  if (operation !== undefined) {
    route = ctx.routes.get(operation);
    named = true;
    if (object === undefined) {
      report(ctx, node, 'unprovable-args', `${callee.getText()} without options`);
    }
  } else if (ts.isPropertyAccessExpression(callee) && Object.hasOwn(verbs, callee.name.text)) {
    ({ route, named } = clientRoute(ctx, node, callee, object));
  }
  if (!named || object === undefined || route === undefined) return;
  if (object.properties.some(ts.isSpreadAssignment)) report(ctx, node, 'unprovable-spread', route);
  if (propertyNamed(object, 'body') !== undefined) {
    ctx.usedRoutes.add(route);
    if (ctx.caps[route] === undefined) report(ctx, node, 'uncapped-route', route);
  }
}

/** Every check that reads a call, in the order findings are reported. */
export function checkCall(ctx: ScanContext, node: ts.CallExpression): void {
  checkCallModule(ctx, node);
  checkIndirectFetch(ctx, node);
  checkRoute(ctx, node);
}
