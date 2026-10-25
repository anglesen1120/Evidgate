// 09.7.1-T1 (plan 09.7.1 §8 S3 "secrets never in logs/UI", §8 T1.1, AC3): the Settings feature
// shows metadata only — a secret-reference row carries `name`, `scope`, `token_prefix`,
// `rotation_state`, `last_rotated_at`, `expires_at`, and the full value is revealed exactly once
// by the shared reveal dialog (09.2.1-T2) and never stored, re-fetched or bound. A store field
// named like the value itself is how that contract is broken by accident, and the mistake is
// invisible in review because a `signal<string>` of the right type compiles. This rule reports
// the name at lint time, where the author still sees the line.
//
// Secret-shaped name. The name is split into words on camelCase, snake_case and kebab-case
// boundaries, lower-cased, and matched case-insensitively. A name is secret-shaped when it is
//
//   - one bare secret word: `token`, `secret`, `password`, `passwd`, `plaintext`, `credential`,
//     `apikey` (`API_KEY` and `apiKey` split to `api` + `key` and match through the compound
//     list below);
//   - one of the exact compound names: `signing_secret`/`signingSecret`, `secretValue`,
//     `private_key`/`privateKey`, `apiKey`, `accessToken`, `refreshToken`, `bearerToken`,
//     `clientSecret` (matched on the joined words, so either spelling and any case matches);
//   - a bare secret word followed by one VALUE word: `value`, `text`, `plain`, `plaintext`,
//     `raw`, `string`, `key` — `tokenValue`, `secret_text`, `passwordRaw`, `secretKey`. The pair
//     counts at ANY position in the name, so a secret word in the middle is reported too:
//     `rawTokenValue`, `signingSecretText`, `pageTokenText` (09.7.1-T2);
//   - a bare secret word in the TRAILING position of a compound name — `ingestToken`,
//     `apiToken`, `webhookSecret`, `rawToken`, `plainSecret`, `pageToken`. Only a presence word in
//     front of it makes the name metadata: `hasToken`, `hasSecret`, `isToken` report whether a
//     value exists, they do not hold it. A presence word in front of a secret word excuses it in
//     every position, so `hasTokenValue` stays legal.
//
// Everything else is metadata and stays legal: `tokenPrefix`, `token_prefix`, `last4`,
// `rotationState`, `secretRef`, `secret_reference`, `secretName`, `tokenName`, `ingestTokens`,
// `tokenCount`, `expiresAt`, `tokenExpiresAt`, `lastRotatedAt`, `ciBindings`. The rule is
// deliberately narrow: a plural (`tokens`) and a secret word with a metadata suffix — `Prefix`,
// `Suffix`, `Ref`, `Reference`, `Name`, `Count`, `At`, `Hash`, `State`, `Id`, `Kind`, `Label` —
// (`passwordHash`, `tokenCount`, `tokenExpiresAt`) are not reported, because a lint rule that
// cries wolf is switched off. A name is split on word boundaries only, so a neutral word that
// merely contains a secret word stays legal: `tokenizer`, `secretary`, `passwordless`
// (`passwordless` is one word after the split, and `passwordLess` splits to `password` + `less`
// with no value word behind it — both describe an authentication mode, not a stored value).
// Every match is a set lookup over the split words or a single-pass regex, so no quantifier in
// this rule can backtrack over the same text twice (ReDoS), and the split is bounded: a name
// longer than `MAX_NAME_CHARS` is examined as its first and last `NAME_WINDOW_CHARS`
// characters, so the work per identifier stays constant (09.7.1-T2).
//
// Reported positions (one message per occurrence; a template reports its first secret-shaped
// name once per property, because a template is one string and one finding is enough to fail):
//   1. an object-literal key of the state literal handed to a `withState(...)`,
//      `patchState(...)` or `signalState(...)` call, nested objects included — the state of a
//      SignalStore. A `signalStore(...)` argument is not walked on its own: its state is the
//      literal of the `withState(...)` calls inside it, each visited once. A state FACTORY or
//      updater is walked too (09.7.1-T2): the object a `withState(() => ({ ... }))` or
//      `patchState(store, (s) => ({ ... }))` argument returns — a concise body, or a top-level
//      `return` of a block body — is the state, and a `signal({ ... })` wrapping it inside that
//      factory is unwrapped. A closure that is not the factory itself is still not state:
//      `withMethods({ save() { post('/x', { token }) } })` sends a payload, it does not store
//      the value, and neither does a nested function inside a returned literal;
//   2. a property signature of an `interface`/`type` whose declaration name matches
//      `/(State|Store)$/` (`interface SecretsState { password: string }`), and a member of the
//      type-argument literal of `withState<{ ... }>(...)`;
//   3. a class field, private field, accessor field, getter or constructor parameter property of
//      a class whose name matches `/Store$/` (`readonly token = signal('')`, `#token = signal('')`,
//      `constructor(private token: string)`, `get signingSecret()`);
//   4. an inline `template:` string of a component: a member read whose property is
//      secret-shaped — `{{ store.token() }}`, `{{ row.secret }}`, `[value]="x.signing_secret"`,
//      `store.plaintext` — reported once per template property;
//   5. a `templateUrl` `.html` file of a component (queue F16): the constant URL is resolved
//      relative to the component file — an absolute or relative `context.filename` both work, the
//      relative one against `process.cwd()` — and the file is read with `node:fs` and scanned
//      with the SAME member-read check as position 4 plus the bare reads of position 6. A file
//      larger than `MAX_TEMPLATE_CHARS`, a missing or unreadable file, an absolute path and any
//      URL with a scheme (`http(s):`, `data:`) are silently skipped: the check never throws and
//      never fetches. Reported once, on the `templateUrl` property's value;
//   6. a BARE identifier read in a template expression, inline or in a `templateUrl` file: an
//      interpolation `{{ token }}`, a property/event/attribute binding `[value]="secret"`,
//      `x="{{ token }}"`, a block header `@if (password)`, `@switch (credential)`, the
//      right-hand side of `@let label = signingSecret;`, the iterated expression of
//      `@for (row of pendingToken; track row.id)`, and a `*ngIf`-style structural directive
//      `*ngIf="secretValue"`. Only an identifier that READS a field counts, so the rule does not
//      report a name that is the root of an object access (`token.prefix`, `token?.prefix` —
//      whose member names are checked as in position 4), a template-reference declaration
//      (`#token`), an object key (`{ token: true }`), a quoted string (`'signingSecret'`), a
//      sigil name (`$index`, `$event`), the declaration variable of an `@for` (`@for (token of
//      tokens)`) or the `as` alias of an `@if` — and no HTML text outside an expression is
//      scanned at all, so the copy `Token is shown once.` stays legal;
//   7. a secret-carrying RESULT type held in state (`secretCarryingType`): `IssuedInvite`,
//      `RotationResult`, `IngestTokenCreatedResponse`, `InviteIssuedView` and
//      `CiBindingCreatedResponse` are the shapes a route answers with a ONE-TIME value, so a
//      field, accessor or constructor parameter property of a `/Store$/` class typed with one is
//      reported at any depth (`Signal<IssuedInvite | null>`, `readonly last: RotationResult`), as
//      is a `signal<...>()`/`computed<...>()`/`withState<...>()` type argument of one in a
//      Settings file and a member type of an `interface`/`type` named `…State|…Store`. A method
//      that RETURNS one (`async rotate(): Promise<RotationResult | null>`) is the sanctioned
//      shape — a one-time value is a return value and never state — so a return type, a method
//      parameter, a local variable and the declaration of the type itself stay legal, and
//      `secrets.store.ts`/`members.store.ts` lint clean.
//
// A top-level `const token = signal('')` outside a `/Store$/` class is NOT reported: a local
// variable is not store state, and reporting every signal would make the rule unusable.
//
// Blind spots (a lint rule is defence in depth, not the only defence):
//   - a template built at run time is not scanned: `template:` must be a constant string (a
//     template literal with `${}` is not folded) and `templateUrl:` must be a constant, relative
//     path — an absolute path, a scheme (`http(s):`, `data:`) and a computed URL are skipped;
//   - a `templateUrl` file is read only up to `MAX_TEMPLATE_CHARS`, a single binding expression
//     only up to `MAX_EXPRESSION_CHARS`, and one template contributes at most
//     `MAX_EXPRESSION_WINDOWS` expressions: a violation past either bound is not seen;
//   - the `track` clause of an `@for` and the `; as alias` clause of an `@if` are not scanned,
//     and neither is the alias of a `let-`/`ref-` microsyntax binding: only the iterated
//     expression, the condition and the binding value are;
//   - a result type reached through a type alias (`type Issued = IssuedInvite`) or a generic
//     parameter is caught only where the NAME itself is written, and a component class field
//     typed with one is not reported (only a `/Store$/` class, a `signal`/`computed`/`withState`
//     generic and a `…State|…Store` member are);
//   - a member read assembled at run time (`row[key]`, `store[this.field]`) has no name in the
//     source to match;
//   - a store field reached only through a string key (`state()['token']`) is not a property
//     name this rule sees;
//   - a name longer than `MAX_NAME_CHARS` is examined only in its first and last
//     `NAME_WINDOW_CHARS` characters: a secret word buried in the middle of a 150-character
//     identifier is not reported. The cap is what keeps the split linear, and no real field name
//     reaches it;
//   - a secret word in the middle of a name is reported only when the word behind it is a VALUE
//     word: `rawTokenValue` and `signingSecretText` are, `rawTokenMaterial` and
//     `nextTokenRefreshAt` are not — the second names when the token refreshes, which is
//     metadata;
//   - a factory whose state is reached through a nested closure (`withState(() => make())`) or
//     through a `return` inside a block other than the factory's own body (`if (x) return {...}`)
//     is not walked: only a concise body and a top-level `return` are;
//   - a `signal({ token: '' })` at field level (`readonly state = signal({ token: '' })` in a
//     `/Store$/` class) is checked as a field NAME only (`state`, which is neutral); the literal
//     inside it is walked only when a state call hands it over, directly or through a factory.
//
// Two reviewed exemptions, both checked against the path segments below the last
// `apps/web/src/app` marker (mirroring `no-bypass-security-trust.cjs`):
//   - a `*.spec.ts` file, which has to spell the forbidden name to assert its absence;
//   - the one-time reveal component: a directory named exactly `reveal` at any depth below
//     `apps/web/src/app`, or a file whose basename carries `reveal` as a whole word between the
//     separators Angular file names use (`reveal-dialog.ts`, `signing-secret-reveal.component.ts`,
//     `reveal.ts`) — that component is the single reviewed place allowed to hold a secret value
//     in memory (09.2.1-T2). The word boundary is what keeps a mere substring from switching the
//     rule off: `revealed.ts`, `unrevealed.ts` and `secrets-revealed-store.ts` are reported like
//     any other file. The anchor is the whole exemption, deliberately: the directory form already
//     exempts every file inside `reveal/`, so adding a store-file exception to the basename form
//     alone would be a rule the path already contradicts.
//
// Registered by apps/web/lint/boundaries.cjs for the Settings feature only, and self-contained
// enough that a bare `Linter` handed the rule directly gets the same answer.
'use strict';

const fs = require('node:fs');
const path = require('node:path');

/** The application root, as the sequence of path segments it must appear as. */
const APP_ROOT_SEGMENTS = ['apps', 'web', 'src', 'app'];

/** The one reviewed component family allowed to hold a secret value, as a directory segment. */
const ALLOWED_REVEAL_DIRECTORY = 'reveal';

/**
 * ... or as a whole word of the file's own name (`signing-secret-reveal.component.ts`,
 * `reveal-dialog.ts`, `reveal.ts`). The word must sit between the start/end of the basename and
 * one of the separators Angular file names use, so a mere substring (`revealed.ts`,
 * `unrevealed.ts`, `secrets-revealed-store.ts`) does not switch the rule off.
 */
const ALLOWED_REVEAL_FILE = /(?:^|[-.])reveal(?:[-.]|$)/;

/** A spec has to spell the forbidden name to assert its absence. */
const SPEC_FILE = /\.spec\.ts$/;

/**
 * The response shapes that carry a ONE-TIME secret value (queue F16, position 7): the app-local
 * result interfaces the stores declare (`IssuedInvite` in members.store.ts, `RotationResult` in
 * secrets.store.ts) and the generated ones the routes answer with (`@evidgate/sdk`
 * `packages/sdk/gen/types.gen.ts`, from `contracts/openapi.yaml`). A field, a signal generic or a
 * state member of one of these holds the value in state, which is what AC3 forbids; a method that
 * returns one is the sanctioned shape.
 */
const SECRET_CARRYING_TYPES = new Set([
  'IssuedInvite',
  'RotationResult',
  'IngestTokenCreatedResponse',
  'InviteIssuedView',
  'CiBindingCreatedResponse',
]);

/** The calls whose type argument a Settings file must not fill with a secret-carrying type. */
const RESULT_TYPE_CALLS = new Set(['signal', 'computed', 'withState']);

/**
 * What one template may contribute, as characters: a `template:` string and a `templateUrl` file
 * are both scanned at most this far, and a file larger than this is not read at all. Bounded work
 * per property, like `MAX_NAME_CHARS` bounds the split (queue F16).
 */
const MAX_TEMPLATE_CHARS = 262_144;

/** What one binding expression may contribute: `{{ ... }}`, a binding value, a block header. */
const MAX_EXPRESSION_CHARS = 4_000;

/**
 * The most expression windows one template contributes. A Settings template writes a few dozen;
 * the cap is what keeps the scan linear on a pathological template (`@switch (` repeated), where
 * every header would otherwise cost a bounded paren scan of its own.
 */
const MAX_EXPRESSION_WINDOWS = 4_096;

/** Bound on the type nodes one annotation may contribute to the result-type walk. */
const MAX_TYPE_NODES = 256;

/**
 * A `templateUrl` that names a scheme is never read: an `http(s)`/`data:` URL is not a local file,
 * and a Windows drive (`C:\...`) is not one either. An absolute path is skipped for the same
 * reason — a template the linter reads is the one the component ships next to itself.
 */
const TEMPLATE_URL_SCHEME = /^[A-Za-z][A-Za-z0-9+.-]*:/;

/** The bare secret words: the whole name is the value. */
const BARE_SECRET_WORDS = new Set([
  'token',
  'secret',
  'password',
  'passwd',
  'plaintext',
  'credential',
  'apikey',
]);

/** The exact compound names, matched on the words joined with no separator. */
const COMPOUND_SECRET_NAMES = new Set([
  'signingsecret',
  'secretvalue',
  'privatekey',
  'apikey',
  'accesstoken',
  'refreshtoken',
  'bearertoken',
  'clientsecret',
]);

/**
 * The words that turn a bare secret word into the value itself: `tokenValue`, `secret_text`,
 * `secretKey`.
 */
const VALUE_WORDS = new Set(['value', 'text', 'plain', 'plaintext', 'raw', 'string', 'key']);

/**
 * The words that, in front of a secret word, describe whether the value exists rather than hold
 * it: `hasToken`, `hasSecret`, `isToken`. A name is metadata when the only secret word it carries
 * is a trailing one with one of these in front of it.
 */
const PRESENCE_WORDS = new Set(['has', 'is', 'no', 'any', 'requires', 'allowed', 'supported']);

/** A state declaration: the interface/type alias that describes a store's state. */
const STATE_DECLARATION = /(State|Store)$/;

/** A SignalStore class: its fields are the state a template can read. */
const STORE_CLASS = /Store$/;

/**
 * The calls whose own argument is a state literal (`STATE_LITERAL_CALLS`): their literal is the
 * one walked. `STATE_CALLS` adds `signalStore`, whose state is the literal of the `withState(...)`
 * calls inside it — the walk stops at every call in that set, so a nested state call is reported
 * by its own visit exactly once.
 */
const STATE_LITERAL_CALLS = new Set(['withState', 'patchState', 'signalState']);
const STATE_CALLS = new Set([...STATE_LITERAL_CALLS, 'signalStore']);

/**
 * The functions a state call's argument may be: the factory (`withState(() => ({ ... }))`) or the
 * updater (`patchState(store, (state) => ({ ... }))`) whose RETURNED literal is the state.
 */
const FACTORY_TYPES = new Set(['ArrowFunctionExpression', 'FunctionExpression']);

/**
 * The calls that wrap a state object without being state themselves: inside a state call's
 * argument, the literal `signal({ ... })` hands over is state too (09.7.1-T2). Nowhere else is a
 * `signal(...)` argument read — a field-level initialiser is checked as a field name only.
 */
const WRAPPED_STATE_CALLS = new Set(['signal']);

/**
 * The split is bounded: a name longer than `MAX_NAME_CHARS` is examined as its first and last
 * `NAME_WINDOW_CHARS` characters, so the work per identifier is constant however long the source
 * name is (09.7.1-T2). A secret word buried between the two windows is not seen — the header's
 * blind spots say so.
 */
const MAX_NAME_CHARS = 128;
const NAME_WINDOW_CHARS = 64;

/** The class member kinds that declare a field a template can read. */
const FIELD_KINDS = new Set([
  'PropertyDefinition',
  'TSAbstractPropertyDefinition',
  'AccessorProperty',
]);

/**
 * A member read inside a template — inline, or in the file a `templateUrl` names: the property
 * name after a `.`, which is what `{{ store.token() }}`, `{{ store?.token }}`, `{{ row.secret }}`,
 * `a.b.token` and `[value]="x.signing_secret"` all have. The dot only counts when it is glued to
 * the member in front of it (an identifier character, or the `)`/`]` that closes a call or an
 * index — with the `?` of an optional chain allowed in between) and to the property name behind
 * it, so the full stop of a sentence is not a member read: `Rotate the key. Token is shown once.`,
 * `Copy it now. Secret values are hidden.` and `e.g. token` stay legal. A bare `{{ token }}` read
 * of a field has no dot and is not matched here: `firstSecretShapedBareRead` below is what
 * reports it, in an interpolation, a binding value or a block header (queue F16). The lookbehind
 * keeps the preceding character out of the match, so `a.b.token` reports `b` and then still
 * reports `token`.
 */
const TEMPLATE_MEMBER_READ = /(?<=[\w$]|\)|\])\??\.([A-Za-z_$][\w$]*)/g;

/**
 * The name — bounded first, see `examinedName` — split into lower-cased words on camelCase,
 * snake_case and kebab-case boundaries.
 */
function nameWords(name) {
  return examinedName(name)
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .split(/[^A-Za-z0-9]+/)
    .filter((word) => word !== '')
    .map((word) => word.toLowerCase());
}

/**
 * The text the split runs over: the name itself while it is short enough, otherwise its first and
 * last window, joined by a separator so the two windows never fuse into one word. Bounded work per
 * identifier, whatever the source length (`MAX_NAME_CHARS`).
 */
function examinedName(name) {
  if (name.length <= MAX_NAME_CHARS) {
    return name;
  }
  return `${name.slice(0, NAME_WINDOW_CHARS)} ${name.slice(-NAME_WINDOW_CHARS)}`;
}

/** Whether a field name names a secret value rather than its metadata. See the header table. */
function isSecretShaped(name) {
  const words = nameWords(name);
  if (words.length === 0) {
    return false;
  }
  const joined = words.join('');
  if (BARE_SECRET_WORDS.has(joined) || COMPOUND_SECRET_NAMES.has(joined)) {
    return true;
  }
  for (let index = 0; index < words.length; index += 1) {
    if (!BARE_SECRET_WORDS.has(words[index])) {
      continue;
    }
    // A presence word in front of it reports whether the value exists (`hasToken`, `isToken`,
    // and `hasTokenValue`): metadata, in every position.
    if (index > 0 && PRESENCE_WORDS.has(words[index - 1])) {
      continue;
    }
    // A VALUE word behind it makes it the value itself, whatever stands in front of it:
    // `tokenValue`, `rawTokenValue`, `signingSecretText`. A metadata word behind it
    // (`tokenCount`, `passwordHash`) and a bare word behind it (`tokenPrefix`) do not.
    const next = words[index + 1];
    if (next !== undefined && VALUE_WORDS.has(next)) {
      return true;
    }
    // A trailing secret word names the value (`ingestToken`, `webhookSecret`, `pageToken`): no
    // word behind it turns it into metadata.
    if (index === words.length - 1 && words.length > 1) {
      return true;
    }
  }
  return false;
}

/**
 * The text of every `{{ ... }}` interpolation in a template, as a window starting where its
 * expression starts. A manual scan rather than a regex, so the work is one pass over the text and
 * no quantifier can backtrack: the closing `}}` is looked up with `indexOf`, and an unterminated
 * or over-long interpolation contributes at most `MAX_EXPRESSION_CHARS`.
 */
function interpolationWindows(text, windows) {
  let index = 0;
  while (index < text.length && windows.length < MAX_EXPRESSION_WINDOWS) {
    const open = text.indexOf('{{', index);
    if (open < 0) {
      return;
    }
    const close = text.indexOf('}}', open + 2);
    if (close < 0) {
      return;
    }
    const start = open + 2;
    windows.push({ start, text: text.slice(start, Math.min(close, start + MAX_EXPRESSION_CHARS)) });
    index = close + 2;
  }
}

/**
 * A property/event/attribute binding whose value Angular evaluates: `[x]="..."`, `(x)="..."`,
 * `[(x)]="..."`, `*ngIf="..."`, `bind-x="..."`, `on-x="..."`, `attr.x="..."`, in either quote.
 * Each alternative starts with a different character and each class excludes its own terminator,
 * so nothing here can backtrack over the same text twice.
 */
const BOUND_ATTRIBUTE =
  /(?:\[\(?[^\]\s"'<>()]{1,80}\)?\]|\([^)\s"'<>]{1,80}\)|\*[^=\s"'<>]{1,80}|bind-[^=\s"'<>]{1,80}|on-[^=\s"'<>]{1,80}|attr\.[^=\s"'<>]{1,80})\s*=\s*(?:"([^"]{0,4000})"|'([^']{0,4000})')/g;

/** The text of every binding value in a template, as a window starting at its first character. */
function attributeWindows(text, windows) {
  for (const match of text.matchAll(BOUND_ATTRIBUTE)) {
    if (windows.length >= MAX_EXPRESSION_WINDOWS) {
      return;
    }
    if (match.index === undefined) {
      continue;
    }
    const value = match[1] ?? match[2];
    if (value === undefined) {
      continue;
    }
    const start = (match.index ?? 0) + match[0].length - value.length - 1;
    windows.push({ start, text: value.slice(0, MAX_EXPRESSION_CHARS) });
  }
}

/** The index of the `)` that closes the `(` at `open`, or -1: a depth count, bounded by the cap. */
function closingParenthesis(text, open) {
  let depth = 0;
  const limit = Math.min(text.length, open + 1 + MAX_EXPRESSION_CHARS);
  for (let index = open; index < limit; index += 1) {
    const char = text[index];
    if (char === '(') {
      depth += 1;
    } else if (char === ')') {
      depth -= 1;
      if (depth === 0) {
        return index;
      }
    }
  }
  return -1;
}

/** The text between `(` and its closing `)`, cut at the first top-level `;` (an `as`/`track` clause). */
function headerExpression(text, open, close) {
  const body = text.slice(open + 1, Math.min(close, open + 1 + MAX_EXPRESSION_CHARS));
  const semicolon = body.indexOf(';');
  return semicolon < 0 ? body : body.slice(0, semicolon);
}

/**
 * The expressions of the block headers Angular evaluates: the condition of `@if (...)` and
 * `@else if (...)` (its `; as alias` clause excluded), the subject of `@switch (...)`, the
 * ITERATED expression of `@for (... of EXPR; track ...)` — not the declaration variable in front
 * of ` of ` — and the right-hand side of `@let name = EXPR;`.
 *
 * Every search is bounded by `MAX_EXPRESSION_CHARS` and every step advances the cursor, so a
 * template that repeats `@let` or `@if` with no `=`/`(` behind it costs one line of work per
 * occurrence instead of a rescan to the end of the file.
 */
function blockWindows(text, windows) {
  for (const keyword of ['@if', '@else if', '@switch', '@for']) {
    let index = 0;
    while (index < text.length && windows.length < MAX_EXPRESSION_WINDOWS) {
      const at = text.indexOf(keyword, index);
      if (at < 0) {
        break;
      }
      const after = at + keyword.length;
      // The parenthesis that opens the header, within the few spaces a header allows after it.
      const lookahead = text.slice(after, Math.min(text.length, after + 8));
      const offset = lookahead.indexOf('(');
      if (offset < 0) {
        index = after;
        continue;
      }
      const open = after + offset;
      const close = closingParenthesis(text, open);
      if (close < 0) {
        index = open + 1;
        continue;
      }
      if (keyword === '@for') {
        const body = headerExpression(text, open, close);
        const of = body.indexOf(' of ');
        index = close + 1;
        if (of < 0) {
          continue;
        }
        windows.push({
          start: open + 1 + of + 4,
          text: body.slice(of + 4, of + 4 + MAX_EXPRESSION_CHARS),
        });
        continue;
      }
      windows.push({ start: open + 1, text: headerExpression(text, open, close) });
      index = close + 1;
    }
  }

  let index = 0;
  while (index < text.length && windows.length < MAX_EXPRESSION_WINDOWS) {
    const at = text.indexOf('@let', index);
    if (at < 0) {
      return;
    }
    const start = at + '@let'.length;
    // The right-hand side is one expression, so it runs at most `MAX_EXPRESSION_CHARS`. A `@let`
    // with no `=` inside that window declares nothing this rule can read, and the cursor moves
    // past the window so the scan stays linear.
    const window = text.slice(start, Math.min(text.length, start + MAX_EXPRESSION_CHARS));
    const equals = window.indexOf('=');
    if (equals < 0) {
      index = start + window.length;
      continue;
    }
    const semicolon = window.indexOf(';', equals);
    const end = semicolon < 0 ? window.length : semicolon;
    windows.push({ start: start + equals + 1, text: window.slice(equals + 1, end) });
    index = start + end + 1;
  }
}

/**
 * The window's text with every quoted literal blanked out, so a name inside a string
 * (`[attr.aria-label]="'signingSecret'"`, `[title]="'Token is shown once.'"`) is not read as an
 * identifier. One linear pass; an escaped character inside a string is blanked with it.
 */
function maskedQuoted(text) {
  const chars = text.split('');
  let quote = null;
  for (let index = 0; index < chars.length; index += 1) {
    const char = chars[index];
    if (quote === null) {
      if (char === "'" || char === '"' || char === '`') {
        quote = char;
        chars[index] = ' ';
      }
      continue;
    }
    if (char === '\\') {
      chars[index] = ' ';
      index += 1;
      if (index < chars.length) {
        chars[index] = ' ';
      }
      continue;
    }
    if (char === quote) {
      quote = null;
    }
    chars[index] = ' ';
  }
  return chars.join('');
}

/** An identifier in a template expression: the name a binding reads or declares. */
const TEMPLATE_IDENTIFIER = /[A-Za-z_$][\w$]*/g;

/** The first non-space character at or after `from`, or the empty string at the end of the text. */
function nextNonSpace(text, from) {
  for (let index = from; index < text.length; index += 1) {
    const char = text[index];
    if (char !== ' ' && char !== '\t' && char !== '\n' && char !== '\r') {
      return char;
    }
  }
  return '';
}

/**
 * The first BARE secret-shaped identifier an expression window reads, or null. A name is a bare
 * read when it stands on its own: not a member of an object (`token.prefix`, `token?.prefix` —
 * the member pass reports those), not a template-reference declaration (`#token`), not a sigil
 * (`$index`, `$event`), not an object key (`{ token: true }`) and not a quoted string. Only the
 * expression windows are scanned, so HTML text outside a binding never reaches this check.
 */
function firstSecretShapedBareRead(window) {
  const masked = maskedQuoted(window);
  for (const match of masked.matchAll(TEMPLATE_IDENTIFIER)) {
    const name = match[0];
    const start = match.index;
    if (start === undefined || name.startsWith('$')) {
      continue;
    }
    const before = start === 0 ? '' : masked[start - 1];
    if (before === '.' || before === '#' || before === '$') {
      continue;
    }
    // An object access root names no value of its own: only its members are checked.
    const rest = masked.slice(start + name.length, start + name.length + 3);
    if (rest.startsWith('.') || rest.startsWith('?.')) {
      continue;
    }
    // An object KEY declares a name; it does not read one.
    if (nextNonSpace(masked, start + name.length) === ':') {
      continue;
    }
    if (isSecretShaped(name)) {
      return name;
    }
  }
  return null;
}

/**
 * The first secret-shaped binding in a template's text, or null: the member reads of position 4
 * over the whole text — which also covers a `store.plaintext` that stands outside any binding —
 * and then the bare reads of position 6, in source order of their expression windows. One name is
 * all a property reports, so the answer stops at the first one found.
 */
function firstSecretShapedBinding(text) {
  const bounded = text.length > MAX_TEMPLATE_CHARS ? text.slice(0, MAX_TEMPLATE_CHARS) : text;
  for (const match of bounded.matchAll(TEMPLATE_MEMBER_READ)) {
    if (isSecretShaped(match[1])) {
      return match[1];
    }
  }
  const windows = [];
  interpolationWindows(bounded, windows);
  attributeWindows(bounded, windows);
  blockWindows(bounded, windows);
  windows.sort((left, right) => left.start - right.start);
  for (const window of windows) {
    const name = firstSecretShapedBareRead(window.text);
    if (name !== null) {
      return name;
    }
  }
  return null;
}

/**
 * The text of the `.html` file a constant `templateUrl` names, or null: a URL with a scheme, an
 * absolute path, a missing or unreadable file and a file over `MAX_TEMPLATE_CHARS` all yield
 * nothing, so the rule never fetches, never throws and never reads an unbounded file. The URL is
 * resolved against the component's own directory — `context.filename` is absolute under the CLI
 * and repository-relative under a bare `Linter`, which is resolved against `process.cwd()`.
 */
function templateUrlText(filename, templateUrl) {
  if (filename === '' || templateUrl === '' || templateUrl.length > MAX_EXPRESSION_CHARS) {
    return null;
  }
  if (TEMPLATE_URL_SCHEME.test(templateUrl) || path.isAbsolute(templateUrl)) {
    return null;
  }
  const absolute = path.isAbsolute(filename) ? filename : path.resolve(process.cwd(), filename);
  const resolved = path.resolve(path.dirname(absolute), templateUrl);
  try {
    const stats = fs.statSync(resolved);
    if (!stats.isFile() || stats.size > MAX_TEMPLATE_CHARS) {
      return null;
    }
    return fs.readFileSync(resolved, 'utf8');
  } catch {
    return null;
  }
}

/**
 * The `TSTypeReference` names of secret-carrying result types inside a type annotation, at any
 * depth: `Signal<IssuedInvite | null>`, `readonly last: RotationResult`,
 * `Promise<IngestTokenCreatedResponse>`. A stack walk, bounded by `MAX_TYPE_NODES`, skipping the
 * keys that carry no AST child.
 */
function secretCarryingTypeNames(node, found) {
  const stack = [node];
  let visited = 0;
  while (stack.length > 0 && visited < MAX_TYPE_NODES) {
    const current = stack.pop();
    visited += 1;
    if (current === null || typeof current !== 'object') {
      continue;
    }
    if (Array.isArray(current)) {
      for (const item of current) {
        stack.push(item);
      }
      continue;
    }
    if (typeof current.type !== 'string') {
      continue;
    }
    if (
      current.type === 'TSTypeReference' &&
      current.typeName?.type === 'Identifier' &&
      SECRET_CARRYING_TYPES.has(current.typeName.name)
    ) {
      found.push(current.typeName);
    }
    for (const [key, value] of Object.entries(current)) {
      if (key === 'parent' || key === 'loc' || key === 'range' || key === 'tokens') {
        continue;
      }
      stack.push(value);
    }
  }
  return found;
}

/**
 * The path's segments, with the separators unified and the `.`/`..` travellers resolved, so
 * neither a Windows filename nor a `..` in the path can move which directories a file is in.
 */
function pathSegments(filename) {
  const segments = [];
  for (const segment of filename.replaceAll('\\', '/').split('/')) {
    if (segment === '' || segment === '.') {
      continue;
    }
    if (segment === '..') {
      // A `..` above the root has nothing to remove; dropping it cannot move a path under
      // `apps/web/src/app`, which is the only prefix that can claim the directory exemption.
      segments.pop();
      continue;
    }
    segments.push(segment);
  }
  return segments;
}

/** The index of the LAST `apps/web/src/app` segment sequence, or -1 when there is none. */
function lastApplicationRoot(segments) {
  for (let start = segments.length - APP_ROOT_SEGMENTS.length; start >= 0; start -= 1) {
    if (APP_ROOT_SEGMENTS.every((segment, offset) => segments[start + offset] === segment)) {
      return start;
    }
  }
  return -1;
}

/**
 * Whether a file is one of the two reviewed exemptions.
 *
 * `context.filename` is whatever ESLint was handed: the CLI passes an absolute path, while the
 * `Linter` of `packages/cli/src/webLintBoundaries.test.ts` passes the repository-relative one
 * verbatim (ESLint 10 `Linter#verify` does not resolve it). The directory exemption reads only
 * the segments BELOW the last `apps/web/src/app` marker, and only as whole segments, so a
 * checkout whose own parent directory is named `reveal`, a `..` that leaves the directory or a
 * look-alike (`not-reveal/`) cannot switch the rule off. A path outside `apps/web/src/app/`
 * never gets the directory exemption; the `*.spec.ts` and `reveal`-word file-name exemptions are
 * basename checks and hold anywhere — the word form is anchored, so `secrets-revealed-store.ts`
 * and `unrevealed.ts` are reported, while `signing-secret-reveal.component.ts` is exempt.
 */
function isExempt(filename) {
  const segments = pathSegments(filename);
  const file = segments.at(-1);
  if (file === undefined) {
    return false;
  }
  if (SPEC_FILE.test(file) || ALLOWED_REVEAL_FILE.test(file)) {
    return true;
  }
  const root = lastApplicationRoot(segments);
  if (root < 0) {
    return false;
  }
  // The last segment is the file itself; only the directories that hold it decide the answer.
  return segments.slice(root + APP_ROOT_SEGMENTS.length, -1).includes(ALLOWED_REVEAL_DIRECTORY);
}

/** The string a constant expression stands for, or null: a string literal or a plain template. */
function constantString(node) {
  if (node.type === 'Literal') {
    return typeof node.value === 'string' ? node.value : null;
  }
  if (node.type === 'TemplateLiteral' && node.expressions.length === 0) {
    return node.quasis[0].value.cooked ?? null;
  }
  return null;
}

/**
 * The property name a key node declares, or null for a computed key with no constant name. A
 * private class field (`#token`) declares its name too: it is still a field the store holds.
 */
function keyName(node) {
  const key = node.key;
  if (!key) {
    return null;
  }
  if (node.computed) {
    return key.type === 'Literal' && typeof key.value === 'string' ? key.value : null;
  }
  if (key.type === 'Identifier' || key.type === 'PrivateIdentifier') {
    return key.name;
  }
  if (key.type === 'Literal' && typeof key.value === 'string') {
    return key.value;
  }
  return null;
}

/** The name a call is made through: `withState(...)`, `store.patchState(...)`, `store['x'](...)`. */
function calleeName(node) {
  const callee = node.callee;
  if (!callee) {
    return null;
  }
  if (callee.type === 'Identifier') {
    return callee.name;
  }
  if (callee.type !== 'MemberExpression') {
    return null;
  }
  if (!callee.computed) {
    return callee.property.type === 'Identifier' ? callee.property.name : null;
  }
  return callee.property.type === 'Literal' && typeof callee.property.value === 'string'
    ? callee.property.value
    : null;
}

/** The nodes that hold runtime code, not state: nothing inside them is a store key. */
const CLOSURE_TYPES = new Set([
  'FunctionExpression',
  'ArrowFunctionExpression',
  'FunctionDeclaration',
  'ClassExpression',
  'ClassDeclaration',
]);

/**
 * The object literals a state literal is made of: the literal itself and the nested objects that
 * are part of it, in source order. The walk stops at a function body — an HTTP payload inside a
 * `withMethods` closure (`post('/x', { token })`) is sent, not stored — and at a nested state call,
 * whose own visit reports its literal once. `parent` is skipped so the walk cannot follow the tree
 * back up; `loc`/`range`/`tokens`/`comments` carry no AST node worth visiting.
 */
function stateLiteralsIn(node, found) {
  if (node === null || typeof node !== 'object') {
    return found;
  }
  if (Array.isArray(node)) {
    for (const item of node) {
      stateLiteralsIn(item, found);
    }
    return found;
  }
  if (typeof node.type !== 'string') {
    return found;
  }
  if (node.type === 'ObjectExpression') {
    found.push(node);
  }
  if (CLOSURE_TYPES.has(node.type)) {
    return found;
  }
  if (node.type === 'CallExpression' && STATE_CALLS.has(calleeName(node) ?? '')) {
    return found;
  }
  for (const [key, value] of Object.entries(node)) {
    if (key === 'parent' || key === 'loc' || key === 'range' || key === 'tokens') {
      continue;
    }
    stateLiteralsIn(value, found);
  }
  return found;
}

/**
 * The expressions a factory returns: the concise body itself, or the argument of each top-level
 * `return` of a block body. A `return` inside a nested block or a nested function is not walked,
 * and neither is a factory that hands its state over through a helper call (header blind spots).
 */
function returnedExpressions(factory) {
  const body = factory.body;
  if (!body) {
    return [];
  }
  if (body.type !== 'BlockStatement') {
    return [body];
  }
  const returned = [];
  for (const statement of body.body) {
    if (statement.type === 'ReturnStatement' && statement.argument) {
      returned.push(statement.argument);
    }
  }
  return returned;
}

/**
 * The object literals a state call's argument hands over as state (09.7.1-T2): the literal itself,
 * the literal a factory or updater RETURNS (`withState(() => ({ token: '' }))`,
 * `patchState(store, (state) => ({ ...state, token }))`), or the literal a `signal({ ... })` inside
 * one wraps. Anything else — a call that is not a `signal`, an identifier, a ternary — yields
 * nothing, so a payload a factory builds for a request is not state.
 */
function stateObjectsIn(expression, found) {
  if (expression === null || typeof expression !== 'object') {
    return found;
  }
  if (expression.type === 'ObjectExpression') {
    found.push(expression);
    return found;
  }
  if (FACTORY_TYPES.has(expression.type)) {
    for (const returned of returnedExpressions(expression)) {
      stateObjectsIn(returned, found);
    }
    return found;
  }
  if (
    expression.type === 'CallExpression' &&
    WRAPPED_STATE_CALLS.has(calleeName(expression) ?? '')
  ) {
    for (const argument of expression.arguments) {
      stateObjectsIn(argument, found);
    }
  }
  return found;
}

/**
 * The identifier a constructor parameter property declares, or null: `constructor(private token:
 * string)` is a field of the instance exactly like a declared one.
 */
function parameterPropertyName(parameter) {
  if (parameter.type !== 'TSParameterProperty') {
    return null;
  }
  const target = parameter.parameter;
  if (target.type === 'Identifier') {
    return target;
  }
  if (target.type === 'AssignmentPattern' && target.left.type === 'Identifier') {
    return target.left;
  }
  return null;
}

/** The type-argument literal of `withState<{ ... }>(...)`, or null. */
function typeArgumentLiteral(node) {
  // typescript-eslint names the node `typeArguments`; `typeParameters` is the pre-6 spelling.
  const argumentsNode = node.typeArguments ?? node.typeParameters;
  const first = argumentsNode?.params?.[0];
  return first?.type === 'TSTypeLiteral' ? first : null;
}

module.exports = {
  meta: {
    type: 'problem',
    docs: {
      description:
        'No secret-shaped field, bare template read or one-time result type in Settings store state or in a template binding.',
    },
    schema: [],
    messages: {
      secretShapedStoreField:
        'Do not name a Settings store field "{{name}}": a Settings store holds metadata only (plan 09.7.1 §8 S3): keep the value server-side or in the one-time reveal component. Bind metadata such as tokenPrefix, secretRef or rotationState instead.',
      secretShapedTemplateBinding:
        'Do not bind "{{name}}" in a Settings template: a Settings store holds metadata only (plan 09.7.1 §8 S3): keep the value server-side or in the one-time reveal component. The reveal dialog is the only place a value is shown, once.',
      secretCarryingType:
        'Do not type Settings state with "{{type}}": a one-time secret result may only be a RETURN value, never state (plan 09.7.1 §8 S3, AC3): keep it out of every field, signal and state member. The reveal dialog shows the value once.',
    },
  },

  create(context) {
    if (isExempt(context.filename || '')) {
      return {};
    }

    // One occurrence, one message: whichever position reaches a key node first, the same node is
    // never reported twice.
    const reported = new WeakSet();

    const reportField = (keyNode, name) => {
      if (reported.has(keyNode) || !isSecretShaped(name)) {
        return;
      }
      reported.add(keyNode);
      context.report({ node: keyNode, messageId: 'secretShapedStoreField', data: { name } });
    };

    /** Reports every secret-shaped key of an object literal, at any depth. */
    const reportObjectLiteral = (literal) => {
      for (const property of literal.properties) {
        // A spread (`...state`) and a computed key have no name to inspect.
        if (property.type !== 'Property') {
          continue;
        }
        const name = keyName(property);
        if (name !== null) {
          reportField(property.key, name);
        }
      }
    };

    /** Reports every secret-shaped member of a type literal body (interface or `<{...}>`). */
    const reportTypeMembers = (members) => {
      for (const member of members) {
        if (member.type !== 'TSPropertySignature') {
          continue;
        }
        const name = keyName(member);
        if (name !== null) {
          reportField(member.key, name);
        }
        // A state member TYPED with a one-time result holds the value itself, so the name check
        // above is not the only one that applies to it (queue F16).
        reportCarryingType(member.typeAnnotation);
      }
    };

    /**
     * Reports every secret-carrying result type inside a type annotation (position 7): a field,
     * accessor or constructor parameter property of a `/Store$/` class, a `signal`/`computed`/
     * `withState` type argument and a `…State|…Store` member all reach one. A method return type
     * never does — that is the sanctioned position for a one-time value.
     */
    function reportCarryingType(annotation) {
      if (annotation === undefined || annotation === null) {
        return;
      }
      for (const typeName of secretCarryingTypeNames(annotation, [])) {
        if (reported.has(typeName)) {
          continue;
        }
        reported.add(typeName);
        context.report({
          node: typeName,
          messageId: 'secretCarryingType',
          data: { type: typeName.name },
        });
      }
    }

    return {
      CallExpression(node) {
        const name = calleeName(node);
        if (name === null) {
          return;
        }
        // Only the literal handed to a state call is state: a `signalStore(...)` argument is a
        // feature list whose `withState(...)` members are visited on their own. A factory or
        // updater argument hands over the literal it returns (09.7.1-T2).
        if (STATE_LITERAL_CALLS.has(name)) {
          for (const argument of node.arguments) {
            for (const handed of stateObjectsIn(argument, [])) {
              for (const literal of stateLiteralsIn(handed, [])) {
                reportObjectLiteral(literal);
              }
            }
          }
        }
        if (name === 'withState') {
          const literal = typeArgumentLiteral(node);
          if (literal !== null) {
            reportTypeMembers(literal.members);
          }
        }
        // `signal<IssuedInvite | null>(null)`, `computed<RotationResult | null>(...)`,
        // `withState<{ last: CiBindingCreatedResponse }>(...)`: the type argument holds the
        // one-time value in state, which is exactly what the return position does not (queue F16).
        if (RESULT_TYPE_CALLS.has(name)) {
          const argumentsNode = node.typeArguments ?? node.typeParameters;
          for (const parameter of argumentsNode?.params ?? []) {
            reportCarryingType(parameter);
          }
        }
      },

      TSInterfaceDeclaration(node) {
        if (STATE_DECLARATION.test(node.id.name)) {
          reportTypeMembers(node.body.body);
        }
      },

      TSTypeAliasDeclaration(node) {
        if (STATE_DECLARATION.test(node.id.name) && node.typeAnnotation.type === 'TSTypeLiteral') {
          reportTypeMembers(node.typeAnnotation.members);
        }
      },

      ClassDeclaration(node) {
        if (node.id === null || !STORE_CLASS.test(node.id.name)) {
          return;
        }
        for (const member of node.body.body) {
          const isGetter = member.type === 'MethodDefinition' && member.kind === 'get';
          if (member.type === 'MethodDefinition' && member.kind === 'constructor') {
            // `constructor(private token: string)` declares a field the store holds, exactly like
            // a property definition; only a parameter property counts, a plain parameter does not.
            for (const parameter of member.value.params) {
              const identifier = parameterPropertyName(parameter);
              if (identifier !== null) {
                reportField(identifier, identifier.name);
                // `constructor(private readonly last: RotationResult) {}` holds the one-time
                // value as a field, so its TYPE is reported too; a plain parameter is neither.
                reportCarryingType(identifier.typeAnnotation);
              }
            }
            continue;
          }
          if (!isGetter && !FIELD_KINDS.has(member.type)) {
            continue;
          }
          const name = keyName(member);
          if (name !== null) {
            reportField(member.key, name);
          }
          // A getter's declared type is a method return type — the sanctioned position for a
          // one-time value — so only a stored field's annotation is checked (queue F16).
          if (!isGetter) {
            reportCarryingType(member.typeAnnotation);
          }
        }
      },

      // A component template is source text, not AST: `{{ store.token() }}` passes every visitor
      // above. The `template:` string is scanned by content, and a `templateUrl:` is resolved and
      // its file read, once per property (queue F16 adds the file and the bare reads).
      Property(node) {
        const key = node.key;
        const isConstantKey = !node.computed;
        const propertyName = !isConstantKey
          ? null
          : key.type === 'Identifier'
            ? key.name
            : key.type === 'Literal' && typeof key.value === 'string'
              ? key.value
              : null;
        if (propertyName !== 'template' && propertyName !== 'templateUrl') {
          return;
        }
        const constant = constantString(node.value);
        if (constant === null) {
          return;
        }
        const text =
          propertyName === 'template'
            ? constant
            : templateUrlText(context.filename ?? '', constant);
        if (text === null) {
          return;
        }
        const name = firstSecretShapedBinding(text);
        if (name !== null) {
          context.report({
            node: node.value,
            messageId: 'secretShapedTemplateBinding',
            data: { name },
          });
        }
      },
    };
  },
};
