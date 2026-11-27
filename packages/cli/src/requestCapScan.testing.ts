/*
 * The syntax scan behind requestCapScan.test.ts: it finds every site that sends a request body
 * and reports the ones without a cap. An SDK operation (or the client's own `post`) called with
 * a `body` must be a row of the cap table; a `fetch` call carrying a body and a `Request` built
 * with one are allowed only in the allow-list's functions; any other transport (`node:http(s)`,
 * `node:http2`, `node:net`, `node:tls`, `node:dgram`, `undici`, `node-fetch`, `axios`, `got`,
 * `ws`, `XMLHttpRequest`, `sendBeacon`) is refused, and a subpath of a transport module
 * (`node:http/promises`, `axios/lib/x`) is the same transport.
 *
 * The scan fails closed: a form it cannot prove body-free is reported, not passed. The alias and
 * `Request` analysis lives in requestCapScan.aliases.testing.ts; it follows a fetch through
 * aliases (`const f = fetch`, assignment, `??`, `||`, `&&`, a ternary, destructuring with a
 * literal key, a default parameter, `as`, `satisfies`, `!`, comma and parentheses), object and
 * class properties holding one, `.call` and `.apply` (also `fetch['call']`), `.bind` without
 * bound arguments, element access, and a `Request` built with a body wherever it goes.
 *
 * An allow-list entry is keyed by `file#function` and exempts only the findings the send path
 * reports there: a direct fetch, a `Request` construction, an unresolved fetch, another
 * transport. It does not exempt SDK-route findings (`uncapped-route`, `unprovable-args`,
 * `unprovable-spread`, `unresolved-url`), which name a route rather than a send.
 *
 * Named limits (passed silently, syntax cannot cross them): a body built in another module and
 * handed to a fetch-like helper not named like one; a fetch alias imported from another file;
 * a callee chosen by a computed (non-literal) name; an SDK operation reached any way but a named
 * import, an alias import or a namespace import's member (`const { op } = sdk`, `const f = op`,
 * a default import, `sdk['op']`, a dynamic `import()`, `.call` on it, `export { op } from`);
 * `Function.prototype.call` held in a variable and used with a fetch; an input of
 * `new Request(input, init)` that is a member read or a call result other than `x.clone()`
 * (`deps.request`); a member read of a `Request`-typed parameter property as the first argument
 * of a fetch (`fetch(this.req)` needs member flow analysis; `fetch(req)` is reported); a fetch
 * whose first argument is an untyped parameter or typed `RequestInfo`; a fetch-named call (not
 * `fetch` itself, not a tracked alias) with more than two arguments, read as another function
 * (`fetchPage(config, fetchImpl, deps)`). Alias and property names are file-wide, so a
 * same-named local that never holds a fetch is still treated as one (fail closed).
 *
 * Reported rather than passed: a `Request` input through `r.clone()`, a conditional, `??`/`||`/`&&`;
 * `Function.prototype.call.call(fetch, ...)`; an SDK operation imported under an alias or through
 * a namespace import; a name rebound by a destructuring, a call result or an untyped declaration
 * with no initializer, or an untyped alias chain, when the file also types the name `Request`; a
 * `new Request` whose direct identifier input is a name no declaration holds or a parameter with
 * no string type (inside a conditional arm or an alias chain such a name is not read).
 *
 * Names resolve by lexical scope: every declaration of the nearest scope is checked, a same-named
 * local in another function is another name, and a use no declaration reaches is checked against
 * every `Request`-typed declaration of the name. A type is a `Request` through its name, a file
 * alias or interface of one, an import of `Request` under another name, `Readonly`, `Required`,
 * `Partial` or `NonNullable` of one, a union or an intersection with one.
 */
import ts from 'typescript';

import { createContext, type Finding } from './requestCapScan.context.testing.js';
import { checkFetch, fetchCall } from './requestCapScan.send.testing.js';
import {
  checkCall,
  checkRequestConstruction,
  checkTaggedFetch,
  checkTransportGlobal,
  checkTransportModule,
} from './requestCapScan.sites.testing.js';

/** `operationName -> "METHOD /path/{param}"` read from the generated operations file. */
export function operationRoutes(text: string): Map<string, string> {
  const routes = new Map<string, string>();
  for (const chunk of text.split('export const ').slice(1)) {
    const match = /^(\w+) = [\s\S]*?\)\.(post|put|patch|delete)<[\s\S]*?\burl: '([^']+)'/.exec(
      chunk,
    );
    if (match === null) continue;
    const name = match[1];
    const verb = match[2];
    const url = match[3];
    if (name === undefined || verb === undefined || url === undefined) continue;
    routes.set(name, `${verb.toUpperCase()} ${url}`);
  }
  return routes;
}

export function scanSource(
  file: string,
  text: string,
  routes: ReadonlyMap<string, string>,
  caps: Readonly<Record<string, number>>,
  allow: Readonly<Record<string, string>>,
): { findings: Finding[]; usedRoutes: Set<string> } {
  const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const ctx = createContext(file, sf, routes, caps, allow);
  const visit = (node: ts.Node): void => {
    checkTransportModule(ctx, node);
    checkTransportGlobal(ctx, node);
    checkRequestConstruction(ctx, node);
    checkTaggedFetch(ctx, node);
    if (ts.isCallExpression(node)) {
      checkCall(ctx, node);
      const target = fetchCall(ctx, node);
      if (target !== undefined) checkFetch(ctx, node, target);
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return { findings: ctx.findings, usedRoutes: ctx.usedRoutes };
}
