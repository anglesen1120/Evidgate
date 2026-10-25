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
//     `raw`, `string`, `key` — `tokenValue`, `secret_text`, `passwordRaw`, `secretKey`;
//   - a bare secret word in the TRAILING position of a compound name — `ingestToken`,
//     `apiToken`, `webhookSecret`, `rawToken`, `plainSecret`. Only a presence word in front of it
//     makes the name metadata: `hasToken`, `hasSecret`, `isToken` report whether a value exists,
//     they do not hold it.
//
// Everything else is metadata and stays legal: `tokenPrefix`, `token_prefix`, `last4`,
// `rotationState`, `secretRef`, `secret_reference`, `secretName`, `tokenName`, `ingestTokens`,
// `tokenCount`, `expiresAt`, `tokenExpiresAt`, `lastRotatedAt`, `ciBindings`. The rule is
// deliberately narrow: a plural (`tokens`) and a secret word with a metadata suffix — `Prefix`,
// `Suffix`, `Ref`, `Reference`, `Name`, `Count`, `At`, `Hash`, `State`, `Id`, `Kind`, `Label` —
// (`passwordHash`, `tokenCount`, `tokenExpiresAt`) are not reported, because a lint rule that
// cries wolf is switched off. Every match is a set lookup over the split words or a single-pass
// regex, so no quantifier in this rule can backtrack over the same text twice (ReDoS).
//
// Reported positions, once per occurrence:
//   1. an object-literal key of the state literal handed to a `withState(...)`,
//      `patchState(...)` or `signalState(...)` call, nested objects included — the state of a
//      SignalStore. A `signalStore(...)` argument is not walked on its own: its state is the
//      literal of the `withState(...)` calls inside it, each visited once. Nothing inside a
//      function body is state: `withMethods({ save() { post('/x', { token }) } })` sends a
//      payload, it does not store the value;
//   2. a property signature of an `interface`/`type` whose declaration name matches
//      `/(State|Store)$/` (`interface SecretsState { password: string }`), and a member of the
//      type-argument literal of `withState<{ ... }>(...)`;
//   3. a class field, private field, accessor field, getter or constructor parameter property of
//      a class whose name matches `/Store$/` (`readonly token = signal('')`, `#token = signal('')`,
//      `constructor(private token: string)`, `get signingSecret()`);
//   4. an inline `template:` string of a component: a member read whose property is
//      secret-shaped — `{{ store.token() }}`, `{{ row.secret }}`, `[value]="x.signing_secret"`,
//      `store.plaintext` — reported once per template property.
//
// A top-level `const token = signal('')` outside a `/Store$/` class is NOT reported: a local
// variable is not store state, and reporting every signal would make the rule unusable.
//
// Blind spots (a lint rule is defence in depth, not the only defence):
//   - a `templateUrl` `.html` file is not scanned by this rule — only an inline `template:`
//     string is read, and the Angular template rules that run over `.html` do not look for a
//     secret-shaped member read;
//   - a member read assembled at run time (`row[key]`, `store[this.field]`) has no name in the
//     source to match;
//   - an inline `template:` that is a template literal with `${}` expressions is not folded, so
//     only a constant template string is scanned;
//   - a store field reached only through a string key (`state()['token']`) is not a property
//     name this rule sees.
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

/** The class member kinds that declare a field a template can read. */
const FIELD_KINDS = new Set([
  'PropertyDefinition',
  'TSAbstractPropertyDefinition',
  'AccessorProperty',
]);

/**
 * A member read inside an inline Angular template: the property name after a `.`, which is what
 * `{{ store.token() }}`, `{{ store?.token }}`, `{{ row.secret }}`, `a.b.token` and
 * `[value]="x.signing_secret"` all have. The dot only counts when it is glued to the member in
 * front of it (an identifier character, or the `)`/`]` that closes a call or an index — with the
 * `?` of an optional chain allowed in between) and to the property name behind it, so the full
 * stop of a sentence is not a member read: `Rotate the key. Token is shown once.`, `Copy it now.
 * Secret values are hidden.` and `e.g. token` stay legal. A bare `{{ token }}` read of a component
 * field has no dot either and is not matched — the class-field position above is what catches it,
 * and only in a `/Store$/` class. The lookbehind keeps the preceding character out of the match,
 * so `a.b.token` reports `b` and then still reports `token`.
 */
const TEMPLATE_MEMBER_READ = /(?<=[\w$]|\)|\])\??\.([A-Za-z_$][\w$]*)/g;

/** The name split into lower-cased words on camelCase, snake_case and kebab-case boundaries. */
function nameWords(name) {
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .split(/[^A-Za-z0-9]+/)
    .filter((word) => word !== '')
    .map((word) => word.toLowerCase());
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
  if (BARE_SECRET_WORDS.has(words[0]) && words.length > 1 && VALUE_WORDS.has(words[1])) {
    return true;
  }
  // A trailing secret word names the value (`ingestToken`, `webhookSecret`) unless the word in
  // front of it reports whether the value exists (`hasToken`). A metadata suffix (`tokenCount`,
  // `tokenExpiresAt`) never reaches this branch: it is not a secret word.
  if (words.length > 1) {
    const last = words.length - 1;
    return BARE_SECRET_WORDS.has(words[last]) && !PRESENCE_WORDS.has(words[last - 1]);
  }
  return false;
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
        'No secret-shaped field in a Settings store, state type or inline template binding.',
    },
    schema: [],
    messages: {
      secretShapedStoreField:
        'Do not name a Settings store field "{{name}}": a Settings store holds metadata only (plan 09.7.1 §8 S3): keep the value server-side or in the one-time reveal component. Bind metadata such as tokenPrefix, secretRef or rotationState instead.',
      secretShapedTemplateBinding:
        'Do not bind "{{name}}" in a Settings template: a Settings store holds metadata only (plan 09.7.1 §8 S3): keep the value server-side or in the one-time reveal component. The reveal dialog is the only place a value is shown, once.',
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
      }
    };

    return {
      CallExpression(node) {
        const name = calleeName(node);
        if (name === null) {
          return;
        }
        // Only the literal handed to a state call is state: a `signalStore(...)` argument is a
        // feature list whose `withState(...)` members are visited on their own.
        if (STATE_LITERAL_CALLS.has(name)) {
          for (const argument of node.arguments) {
            if (argument.type !== 'ObjectExpression') {
              continue;
            }
            for (const literal of stateLiteralsIn(argument, [])) {
              reportObjectLiteral(literal);
            }
          }
        }
        if (name === 'withState') {
          const literal = typeArgumentLiteral(node);
          if (literal !== null) {
            reportTypeMembers(literal.members);
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
        }
      },

      // An inline Angular template is source text, not AST: `{{ store.token() }}` passes every
      // visitor above. The property value is scanned by content instead, once per property.
      Property(node) {
        const key = node.key;
        const isTemplate =
          !node.computed &&
          ((key.type === 'Identifier' && key.name === 'template') ||
            (key.type === 'Literal' && key.value === 'template'));
        if (!isTemplate) {
          return;
        }
        const template = constantString(node.value);
        if (template === null) {
          return;
        }
        for (const match of template.matchAll(TEMPLATE_MEMBER_READ)) {
          const name = match[1];
          if (isSecretShaped(name)) {
            context.report({
              node: node.value,
              messageId: 'secretShapedTemplateBinding',
              data: { name },
            });
            return;
          }
        }
      },
    };
  },
};
