/*
 * The fetch half of the request-cap scan (requestCapScan.testing.ts): how a call reaches a fetch
 * (`fetch`, `.call`, `.apply`, `Reflect.apply`, `.bind`) and whether its request can carry a body.
 */
import ts from 'typescript';

import { fetchKind, memberName, propertyNamed, unwrap } from './requestCapScan.aliases.testing.js';
import { type ScanContext, reportSend } from './requestCapScan.context.testing.js';
import {
  isRequestConstruction,
  requestBodyReason,
  typedRequestReason,
} from './requestCapScan.requests.testing.js';

export type FetchTarget = { form: string; via: 'call' | 'apply' | undefined; exact: boolean };

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

/** The fetch callee of a call and how its arguments are reached, or undefined. */
export function fetchCall(ctx: ScanContext, call: ts.CallExpression): FetchTarget | undefined {
  const callee = unwrap(call.expression);
  const name = memberName(callee);
  const receiver =
    ts.isPropertyAccessExpression(callee) || ts.isElementAccessExpression(callee)
      ? callee.expression
      : undefined;
  if ((name === 'call' || name === 'apply') && receiver !== undefined) {
    const kind = fetchKind(receiver, ctx.refs);
    if (kind !== undefined) {
      return { form: `${receiver.getText()}.${name}`, via: name, exact: kind === 'exact' };
    }
  }
  const kind = fetchKind(callee, ctx.refs);
  return kind === undefined
    ? undefined
    : { form: callee.getText().slice(0, 60), via: undefined, exact: kind === 'exact' };
}

/** The fetch handed to `Reflect.apply`, whose argument list syntax cannot read, or undefined. */
export function reflectApplyTarget(
  ctx: ScanContext,
  call: ts.CallExpression,
): ts.Expression | undefined {
  const callee = unwrap(call.expression);
  if (memberName(callee) !== 'apply') return undefined;
  if (!ts.isPropertyAccessExpression(callee) && !ts.isElementAccessExpression(callee)) {
    return undefined;
  }
  const owner = unwrap(callee.expression);
  const ownerName = ts.isIdentifier(owner) ? owner.text : memberName(owner);
  if (ownerName !== 'Reflect') return undefined;
  const target = call.arguments[0];
  if (target === undefined || fetchKind(unwrap(target), ctx.refs) === undefined) return undefined;
  return target;
}

/** `Function.prototype.call.call(fetch, ...)`: a call/apply/bind of call/apply with a fetch as `this`. */
export function callsWithFetchAsThis(
  callee: ts.Expression,
  owner: ts.Expression | undefined,
  first: ts.Expression,
  ctx: ScanContext,
): boolean {
  const viaOwner = owner === undefined ? undefined : memberName(unwrap(owner));
  return (
    ['call', 'apply', 'bind'].includes(memberName(callee) ?? '') &&
    (viaOwner === 'call' || viaOwner === 'apply') &&
    fetchKind(unwrap(first), ctx.refs) !== undefined
  );
}

/** Judges the first argument of a fetch; returns true when a finding was reported. */
function checkFirstArgument(
  ctx: ScanContext,
  first: ts.Expression,
  send: (kind: string, why: string) => void,
): boolean {
  if (isRequestConstruction(first, ctx.constructors)) {
    ctx.handled.add(first);
    const why = requestBodyReason(first, ctx.requestTyped, ctx.constructors);
    if (why === undefined) return false;
    send('uncapped-fetch', `${why} as the first argument`);
    return true;
  }
  if (ts.isIdentifier(first)) {
    const why = typedRequestReason(first, ctx.requestTyped, ctx.constructors);
    if (why === undefined) return false;
    send('uncapped-fetch', why);
    return true;
  }
  if (ts.isCallExpression(first) || ts.isAwaitExpression(first)) {
    send('unresolved-fetch', 'first argument is computed');
    return true;
  }
  return false;
}

export function checkFetch(ctx: ScanContext, call: ts.CallExpression, target: FetchTarget): void {
  const args = effectiveArguments(call, target.via);
  const send = (kind: string, why: string): void => reportSend(ctx, call, kind, target.form, why);
  if (args === undefined) return send('unresolved-fetch', 'arguments are not an array literal');
  // Only `fetch`, a tracked alias or `.call`/`.apply` on one takes exactly two arguments; a
  // longer fetch-named helper is read as another function (see the header's named limits).
  if (!target.exact && args.length > 2) return;
  if (args.some(ts.isSpreadElement)) return send('unresolved-fetch', 'spread argument');
  const first = args[0] === undefined ? undefined : unwrap(args[0]);
  const second = args[1] === undefined ? undefined : unwrap(args[1]);
  if (first !== undefined && checkFirstArgument(ctx, first, send)) return;
  if (second === undefined) return;
  if (!ts.isObjectLiteralExpression(second)) {
    return send('uncapped-fetch', 'init is not an object literal');
  }
  if (propertyNamed(second, 'body') !== undefined) return send('uncapped-fetch', 'body in init');
  if (second.properties.some(ts.isSpreadAssignment)) send('uncapped-fetch', 'spread in init');
}
