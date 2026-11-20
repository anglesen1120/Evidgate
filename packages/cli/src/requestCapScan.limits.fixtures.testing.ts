/*
 * Fixtures for the request-cap scan's declaration handling and the limits it turned into flags.
 * Each source is a whole module. A row is [name, bad, twin, kind, words]: a form that used to
 * pass (or be misread), its body-free twin, and the words the failure text must contain.
 */
export type LimitForm = readonly [
  name: string,
  bad: string,
  twin: string,
  kind: string,
  words: string,
];

/** Names with several `Request`-typed declarations: every one is checked, not the first. */
export const everyDeclarationForms: readonly LimitForm[] = [
  [
    'a later declaration of the name with an unproven initialiser',
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); } function b(o: Request) { const r: Request = o; fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); } function b(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'without a visible Request construction',
  ],
  [
    'a later parameter of the name with no initialiser',
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); } function b(r: Request) { fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); } function b(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'Request-typed first argument',
  ],
  [
    'a second var declaration of the name in one scope',
    "function a(u: string, o: Request) { var r: Request = new Request(u, { method: 'GET' }); var r: Request = o; fetch(r); }",
    "function a(u: string) { var r: Request = new Request(u, { method: 'GET' }); var r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'without a visible Request construction',
  ],
  [
    'a reassignment in the same scope reached from a closure',
    "function a(u: string, o: Request) { let r: Request = new Request(u, { method: 'GET' }); const swap = () => { r = o; }; swap(); fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a var reassigned in a block and used after it',
    "function a(u: string, o: Request) { var r: Request = new Request(u, { method: 'GET' }); if (u) { r = o; } fetch(r); }",
    "function a(u: string) { var r: Request = new Request(u, { method: 'GET' }); if (u) { fetch(r); } }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a for-of target reassigning a typed name',
    "function a(u: string, list: Request[]) { let r: Request = new Request(u, { method: 'GET' }); for (r of list) { fetch(r); } }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a destructuring assignment reassigning a typed name',
    "function a(u: string, o: { r: Request }) { let r: Request = new Request(u, { method: 'GET' }); ({ r } = o); fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a typed name rebound by destructuring',
    'function a(r: Request) { return r; } function b(o: { r: Request }) { const { r } = o; fetch(r); }',
    'function a(r: Request) { return r; } function b(u: string) { const r = u; fetch(r); }',
    'uncapped-fetch',
    'rebound by destructuring',
  ],
  [
    'a use no declaration reaches',
    'function a(r: Request) { return r; } fetch(r);',
    'function a(r: Request) { return r; } fetch(u);',
    'uncapped-fetch',
    'Request-typed first argument',
  ],
  [
    'a body var shadowing an outer Request in a parameter default',
    'function typed(r: Request) { function inner(x = fetch(r)) { var r; return x; } inner(); }',
    'function typed(u: string) { function inner(x = fetch(u)) { var u; return x; } inner(); }',
    'uncapped-fetch',
    'Request-typed first argument',
  ],
  [
    'a reassigned untyped alias of a Request',
    'function typed(r: Request) { return r; } function send(o: Request) { let r = o; r = o; fetch(r); }',
    'function typed(r: Request) { return r; } function send(u: string) { let r = u; r = u; fetch(r); }',
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'an untyped local holding a member read shadowing a typed name',
    'function a(req: Request) { return req; } function b(d: { request: Request }) { const req = d.request; fetch(req); }',
    "function a(req: Request) { return req; } function b(d: { n: string }) { const req = 'x'; fetch(req); }",
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
  [
    'a typed name overwritten through a parenthesised target',
    "function a(u: string, o: Request) { let r: Request = new Request(u, { method: 'GET' }); (r) = o; fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a typed name overwritten through an as-cast target',
    "function a(u: string, o: Request) { let r: Request = new Request(u, { method: 'GET' }); (r as Request) = o; fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a typed name overwritten through an array pattern',
    "function a(u: string, o: Request[]) { let r: Request = new Request(u, { method: 'GET' }); [r] = o; fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a typed name overwritten through a rest element',
    "function a(u: string, o: Request[]) { let r: Request = new Request(u, { method: 'GET' }); [...r] = o; fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a typed name overwritten through an object rest',
    "function a(u: string, o: { r: Request }) { let r: Request = new Request(u, { method: 'GET' }); ({ ...r } = o); fetch(r); }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'a typed name overwritten through a for-in head',
    "function a(u: string, o: Record<string, Request>) { let r: Request = new Request(u, { method: 'GET' }); for (r in o) { fetch(r); } }",
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); fetch(r); }",
    'uncapped-fetch',
    'reassigned after its declaration',
  ],
  [
    'two Request constructions feeding each other',
    "function a(u: string) { const x: Request = new Request(y, { method: 'GET' }); const y: Request = new Request(x, { method: 'GET' }); fetch(x); }",
    "function a(u: string) { const x: Request = new Request(u, { method: 'GET' }); fetch(x); }",
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'a for-of binding shadowing a typed name',
    'function a(r: Request) { return r; } function b(list: unknown[]) { for (const r of list) { fetch(r); } }',
    'function a(r: Request) { return r; } function b(u: string) { fetch(u); }',
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
  [
    'an untyped alias overwritten with a Request',
    'function f(u: string, o: Request) { let r = u; r = o; fetch(r); }',
    'function f(u: string, v: string) { let r = u; r = v; fetch(r); }',
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'an untyped alias overwritten through a logical assignment',
    'function f(u: string, o: Request) { let r = u; r ||= o; fetch(r); }',
    'function f(u: string, v: string) { let r = u; r ||= v; fetch(r); }',
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'a RequestInfo-typed shadow of a typed name',
    'function a(req: Request) { return req; } function b(req: RequestInfo) { fetch(req); }',
    'function a(req: Request) { return req; } function b(req: string) { fetch(req); }',
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
  [
    'an any-typed shadow overwritten with a Request',
    "function a(req: Request) { return req; } function b(o: Request) { let req: any = 'x'; req = o; fetch(req); }",
    "function a(req: Request) { return req; } function b(v: string) { let req: any = 'x'; req = v; fetch(req); }",
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'a function declaration overwritten with a Request',
    'function a(req: Request) { return req; } function b(o: Request) { function req() { return 1; } req = o; fetch(req); }',
    'function a(req: Request) { return req; } function b(v: string) { function req() { return 1; } req = v; fetch(req); }',
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'an untyped local copying an untyped parameter',
    'function a(req: Request) { return req; } function b(d) { const req = d; fetch(req); }',
    'function a(req: Request) { return req; } function b(d: string) { const req = d; fetch(req); }',
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
  [
    'an untyped local asserted to Request',
    'function a(req: Request) { return req; } function b(h) { const req = h as Request; fetch(req); }',
    'function a(req: Request) { return req; } function b(h: string) { const req = h; fetch(req); }',
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
  [
    'an untyped parameter defaulting to an unresolved name',
    'function a(req: Request) { return req; } function b(req = url) { fetch(req); }',
    "function a(req: Request) { return req; } function b(req = 'x') { fetch(req); }",
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
  [
    'an untyped alias whose later assignment is unknown',
    "function a(req: Request) { return req; } function b(d) { let x = 'a'; x = d; const req = x; fetch(req); }",
    "function a(req: Request) { return req; } function b(d: string) { let x = 'a'; x = d; const req = x; fetch(req); }",
    'uncapped-fetch',
    'rebound by an untyped declaration',
  ],
];

/** Same-named locals in other functions: not one declaration, so one does not taint the other. */
export const shadowedCleanSources: readonly (readonly [name: string, source: string])[] = [
  [
    'a reassignment in another function',
    "function a(u: string, o: Request) { let request: Request = new Request(u, { method: 'GET' }); request = o; return request; } function b(u: string) { const request: Request = new Request(u, { method: 'GET' }); fetch(request); }",
  ],
  [
    'a bare parameter in another function',
    "function a(request: Request) { return request; } function b(u: string) { const request: Request = new Request(u, { method: 'GET' }); fetch(request); }",
  ],
  [
    'an untyped inner local shadowing a typed parameter',
    'function a(request: Request) { const send = (u: string) => { const request = u; fetch(request); }; return send; }',
  ],
  [
    'a catch binding in another function',
    "function a(u: string) { const r: Request = new Request(u, { method: 'GET' }); try { fetch(r); } catch (e) { return e; } }",
  ],
  [
    'a function declaration with a typed name used elsewhere',
    "function r() { return 1; } function a(u: string) { const q: Request = new Request(u, { method: 'GET' }); fetch(q); } function b() { return r(); }",
  ],
  [
    'a conditional reading one constructed Request on both sides',
    "function a(u: string, ok: boolean) { const x: Request = new Request(u, { method: 'GET' }); fetch(new Request(ok ? x : x, { method: 'GET' })); }",
  ],
  [
    'a primitive-typed parameter of the same name',
    'function a(r: Request) { return r; } function b(r: string) { fetch(r); }',
  ],
  [
    'a function declaration of the same name',
    'function a(r: Request) { return r; } function b() { function r() { return 1; } fetch(r); }',
  ],
  [
    'a loop head binding used only inside the loop',
    "function a(u: string, list: string[]) { const r: Request = new Request(u, { method: 'GET' }); for (const r of list) { u; } fetch(r); }",
  ],
];

/** Limits the scan now reports instead of passing. */
export const closedLimitForms: readonly LimitForm[] = [
  [
    'a body inherited via r.clone()',
    "function f(r: Request) { fetch(new Request(r.clone(), { method: 'GET' })); }",
    "function f(u: string) { fetch(new Request(u, { method: 'GET' })); }",
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'a body inherited through a ternary input',
    "function f(r: Request, u: string, ok: boolean) { fetch(new Request(ok ? r : u, { method: 'GET' })); }",
    "function f(u: string, v: string, ok: boolean) { fetch(new Request(ok ? v : u, { method: 'GET' })); }",
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'a body inherited through an untyped alias of a Request',
    "function f(r: Request) { const a = r; fetch(new Request(a, { method: 'GET' })); }",
    "function f(u: string) { const a = u; fetch(new Request(a, { method: 'GET' })); }",
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'a Request alias of an alias reaching a construction',
    "function f(r: Request) { const a = r; const b = a; fetch(new Request(b, { method: 'GET' })); }",
    "function f(u: string) { const a = u; const b = a; fetch(new Request(b, { method: 'GET' })); }",
    'uncapped-fetch',
    'Request inherits a body',
  ],
  [
    'Function.prototype.call.call with a fetch',
    "function f(u: string) { Function.prototype.call.call(fetch, null, u, { body: 'x' }); }",
    'function f(u: string, parse: () => void) { Function.prototype.call.call(parse, null, u); }',
    'unresolved-fetch',
    'fetch handed to call',
  ],
  [
    'an SDK operation through an import alias',
    "import { postWidgets as pw } from './sdk.gen.js'; function f(client: C) { return pw({ client, body: { a: 1 } }); }",
    "import { postWidgets as pw } from './sdk.gen.js'; function f(client: C) { return pw({ client }); }",
    'uncapped-route',
    'POST /widgets',
  ],
  [
    'an SDK operation through a namespace import',
    "import * as sdk from './sdk.gen.js'; function f(client: C) { return sdk.postWidgets({ client, body: { a: 1 } }); }",
    "import * as sdk from './sdk.gen.js'; function f(client: C) { return sdk.postWidgets({ client }); }",
    'uncapped-route',
    'POST /widgets',
  ],
  [
    'an SDK operation through a parenthesised alias callee',
    "import { postWidgets as pw } from './sdk.gen.js'; function f(client: C) { return (pw)({ client, body: { a: 1 } }); }",
    "import { postWidgets as pw } from './sdk.gen.js'; function f(client: C) { return (pw)({ client }); }",
    'uncapped-route',
    'POST /widgets',
  ],
];
