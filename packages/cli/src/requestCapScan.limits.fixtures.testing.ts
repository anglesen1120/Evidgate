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
];
