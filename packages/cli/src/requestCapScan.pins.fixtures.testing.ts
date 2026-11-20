/*
 * Pins for the flag direction of single lines of the `Request` analysis. Each row is a form the
 * scan must report, its body-free twin, the finding kind and the words the report must contain; a
 * change that flips the line turns the form or its twin red. A body-free twin that must stay clean
 * is paired with a form that must stay flagged so both directions of a branch are held.
 */
import type { IndirectForm } from './requestCapScan.fixtures.testing.js';

/** A typed `r` elsewhere in the file, so an untyped `r` is "rebound" and must be proven. */
const typedR = 'function typed(r: Request) { return r; } ';
const rebound = 'rebound by an untyped declaration';
const destructured = 'rebound by destructuring';

const untypedRows = (
  name: string,
  declarations: string,
  twinDeclarations: string,
  words: string,
): IndirectForm => [
  name,
  `${typedR}function leak(ok: boolean, deps: { request: unknown }, x: { o: string }, mk: () => unknown) { ${declarations} fetch(r); }`,
  `${typedR}function ok(ok: boolean, deps: { request: unknown }, x: { o: string }, mk: () => unknown) { ${twinDeclarations} fetch(r); }`,
  'uncapped-fetch',
  words,
];

export const pinForms: ReadonlyArray<IndirectForm> = [
  [
    'a body-carrying construction held by an untyped name',
    `${typedR}function leak(u: string) { const r = new Request(u, { body: 'x' }); fetch(r); }`,
    `${typedR}function ok(u: string) { const r = new Request(u, { method: 'GET' }); fetch(r); }`,
    'uncapped-fetch',
    'Request with a body',
  ],
  [
    'a Request type in parentheses',
    'function leak(r: (Request)) { fetch(r); }',
    'function ok(r: (string)) { fetch(r); }',
    'uncapped-fetch',
    'Request-typed first argument',
  ],
  untypedRows(
    'an object destructuring target with a renamed key',
    "let r = 'a'; ({ o: r } = x);",
    "let r = 'a'; r = 'b';",
    destructured,
  ),
  untypedRows(
    'an array destructuring target with a default',
    "let r = 'a'; [r = 'b'] = [x];",
    "let r = 'a'; r = 'b';",
    destructured,
  ),
  untypedRows(
    'a compound write through a parenthesized target',
    "let r = 'a'; (r) ??= 'b';",
    "let r = 'a'; (r) = 'b';",
    destructured,
  ),
  untypedRows(
    'a ??= of a call result',
    "let r = 'a'; r ??= mk();",
    "let r = 'a'; r ??= 'b';",
    rebound,
  ),
  untypedRows(
    'a &&= of a call result',
    "let r = 'a'; r &&= mk();",
    "let r = 'a'; r &&= 'b';",
    rebound,
  ),
  untypedRows(
    'a conditional with an unreadable false arm',
    "let r = ok ? 'a' : deps.request;",
    "let r = ok ? 'a' : 'b';",
    rebound,
  ),
  untypedRows(
    'a conditional with an unreadable true arm',
    "let r = ok ? deps.request : 'a';",
    "let r = ok ? 'b' : 'a';",
    rebound,
  ),
  [
    'a copy of an untyped parameter with a default',
    `${typedR}function leak(p = 'x') { const r = p; fetch(r); }`,
    `${typedR}function ok(p: string = 'x') { const r = p; fetch(r); }`,
    'uncapped-fetch',
    rebound,
  ],
  untypedRows(
    'a copy of a name written by destructuring',
    "let o = 'a'; ({ o } = x); const r = o;",
    "let o = 'a'; o = 'b'; const r = o;",
    rebound,
  ),
  untypedRows(
    'a copy of a name that never gets a value',
    'let o; const r = o;',
    "let o = 'a'; const r = o;",
    rebound,
  ),
  untypedRows(
    'a copy through a cycle of untyped names',
    "let a = 'x'; let b = a; a = b; const r = b;",
    "let a = 'x'; let b = a; a = 'y'; const r = b;",
    rebound,
  ),
  ...(
    [
      ['??', 'u ?? inb', 'u ?? u2', 'right'],
      ['||', 'u || inb', 'u || u2', 'right'],
      ['&&', 'u && inb', 'u && u2', 'right'],
      ['??', 'inb ?? u', 'u2 ?? u', 'left'],
      ['||', 'inb || u', 'u2 || u', 'left'],
      ['&&', 'inb && u', 'u2 && u', 'left'],
    ] as const
  ).map(([op, bad, twin, side]): IndirectForm => [
    `a ${op} input with a Request on the ${side}`,
    `function leak(u: string, u2: string, inb: Request) { fetch(new Request(${bad}, { method: 'GET' })); }`,
    `function ok(u: string, u2: string, inb: Request) { fetch(new Request(${twin}, { method: 'GET' })); }`,
    'uncapped-fetch',
    'Request inherits a body',
  ]),
  ...(
    [
      ['false', 'ok ? u : inb', 'ok ? u : u2'],
      ['true', 'ok ? inb : u', 'ok ? u2 : u'],
    ] as const
  ).map(([arm, bad, twin]): IndirectForm => [
    `a conditional input with a Request in the ${arm} arm`,
    `function leak(ok: boolean, u: string, u2: string, inb: Request) { fetch(new Request(${bad}, { method: 'GET' })); }`,
    `function ok(ok: boolean, u: string, u2: string, inb: Request) { fetch(new Request(${twin}, { method: 'GET' })); }`,
    'uncapped-fetch',
    'Request inherits a body',
  ]),
];

/** Type forms that name a `Request` without the bare name; the twin types the same shape `string`. */
const typeForms: ReadonlyArray<readonly [string, string, string]> = [
  ['a Readonly wrapper', 'Readonly<Request>', 'Readonly<string>'],
  ['a Required wrapper', 'Required<Request>', 'Required<string>'],
  ['a Partial wrapper', 'Partial<Request>', 'Partial<string>'],
  ['a NonNullable wrapper', 'NonNullable<Request>', 'NonNullable<string>'],
  ['a nested wrapper', 'Readonly<Partial<Request>>', 'Readonly<Partial<string>>'],
  ['an intersection', 'Request & { tag: string }', 'string & { tag: string }'],
  ['a qualified name in a wrapper', 'Readonly<globalThis.Request>', 'Readonly<globalThis.String>'],
];

const aliasDeclarations: ReadonlyArray<readonly [string, string, string]> = [
  ['a type alias', 'type R = Request;', 'type R = string;'],
  ['an alias of an alias', 'type A = Request; type R = A;', 'type A = string; type R = A;'],
  [
    'an alias declared after its use',
    'type R = A; type A = Request;',
    'type R = A; type A = string;',
  ],
  ['an alias of a wrapper', 'type R = Readonly<Request>;', 'type R = Readonly<string>;'],
  [
    'an import under another name',
    "import type { Request as R } from './web.js';",
    "import type { Headers as R } from './web.js';",
  ],
  [
    'an interface extending Request',
    'interface R extends Request {}',
    'interface R { tag: string }',
  ],
  [
    'an interface extending an alias',
    'type A = Request; interface R extends A {}',
    'type A = string; interface R { tag: A }',
  ],
];

export const typeResolutionForms: ReadonlyArray<IndirectForm> = [
  ...typeForms.map(([name, bad, twin]): IndirectForm => [
    `a parameter typed through ${name}`,
    `function leak(r: ${bad}) { fetch(r); }`,
    `function ok(r: ${twin}) { fetch(r); }`,
    'uncapped-fetch',
    'Request-typed first argument',
  ]),
  ...aliasDeclarations.map(([name, bad, twin]): IndirectForm => [
    `a parameter typed through ${name}`,
    `${bad} function leak(r: R) { fetch(r); }`,
    `${twin} function ok(r: R) { fetch(r); }`,
    'uncapped-fetch',
    'Request-typed first argument',
  ]),
  ...aliasDeclarations.map(([name, bad, twin]): IndirectForm => [
    `a local typed through ${name} built from a call`,
    `${bad} function leak(mk: () => R) { const r: R = mk(); fetch(r); }`,
    `${twin} function ok(mk: () => R) { const r: R = mk(); fetch(r); }`,
    'uncapped-fetch',
    'without a visible Request construction',
  ]),
  [
    'a parameter property typed Request',
    'class C { constructor(private req: Request) { fetch(req); } }',
    'class C { constructor(private req: string) { fetch(req); } }',
    'uncapped-fetch',
    'Request-typed first argument',
  ],
];

/** A `Request` input that is a bare name nothing in the file declares as a string or a Request. */
export const unreadInputForms: ReadonlyArray<IndirectForm> = [
  [
    'a Request input no declaration holds, sent',
    "function leak() { fetch(new Request(d, { method: 'GET' })); }",
    "function ok() { const d = 'x'; fetch(new Request(d, { method: 'GET' })); }",
    'uncapped-fetch',
    'not a declared name',
  ],
  [
    'a Request input no declaration holds, built',
    "function leak() { return new Request(d, { method: 'GET' }); }",
    "function ok() { const d = 'x'; return new Request(d, { method: 'GET' }); }",
    'uncapped-request',
    'not a declared name',
  ],
  [
    'a Request input that is an untyped parameter',
    "function leak(d) { fetch(new Request(d, { method: 'GET' })); }",
    "function ok(d: string) { fetch(new Request(d, { method: 'GET' })); }",
    'uncapped-fetch',
    'parameter with no string type',
  ],
  [
    'a Request input that is an untyped parameter with a default',
    "const leak = (d = 'x') => new Request(d, { method: 'GET' });",
    "const ok = (d: string = 'x') => new Request(d, { method: 'GET' });",
    'uncapped-request',
    'parameter with no string type',
  ],
];
