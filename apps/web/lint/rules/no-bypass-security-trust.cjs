// 09.1.1-T10 (AC7, ADR-0008 rule 4; 00 §5.6 W6, FR-09-15): the application origin runs under
// the nonce CSP + Trusted Types policy of apps/web/scripts/app-origin-csp.json, whose
// `trusted-types` list is deliberately `angular` only — no `angular#unsafe-bypass`. A
// `bypassSecurityTrust*` value therefore cannot reach a DOM sink in the browser: it throws a
// Trusted Types violation at run time. This rule reports the bypass at lint time instead,
// where the author still sees the line to replace, because a TypeError raised at a component
// render is far harder to trace than a lint error on the call that caused it.
//
// Every syntactic form that reaches a bypass method is reported, once per occurrence:
//   - a call through a member, `sanitizer.bypassSecurityTrustHtml(html)`, including optional
//     chaining (`sanitizer?.bypassSecurityTrustHtml(html)`) and computed string access
//     (`sanitizer['bypassSecurityTrustHtml'](html)`);
//   - a member used as a value, which is how an alias is built:
//     `const bind = sanitizer.bypassSecurityTrustHtml;`
//   - a bare identifier call (`bypassSecurityTrustHtml(html)`), e.g. the local binding of a
//     destructuring;
//   - a destructuring of the member: `const { bypassSecurityTrustHtml } = sanitizer;`
//
// Only the five DomSanitizer method names are inspected, so a local helper genuinely named
// `bypassSecurityTrustFoo` stays legal.
//
// W6 permits the bypass in exactly one reviewed place: the 06 artifact viewer, which renders
// untrusted evidence inside an isolated origin (FR-09-15). That component is owned by
// 09.4.1-T5 and lives in a directory named `artifact-viewer-bridge`; such a directory below
// `apps/web/src/app` is allow-listed here (see ALLOWED_BRIDGE_DIRECTORY).
//
// Registered by apps/web/lint/boundaries.cjs, and self-contained enough that a bare `Linter`
// handed the blocks directly gets the same answer (the allow-list guard below).
'use strict';

/**
 * The one reviewed directory a bypass may live in, as an exact path segment below
 * `apps/web/src/app`. 09.4.1-T5 owns that component and either keeps this directory name or
 * amends this constant as part of its own review.
 */
const ALLOWED_BRIDGE_DIRECTORY = 'artifact-viewer-bridge';

/** The DomSanitizer methods that switch Angular's sanitization off. */
const BYPASS_NAME = /^bypassSecurityTrust(Html|Style|Script|Url|ResourceUrl)$/;

/**
 * Whether a file may use a bypass. The path prefix is matched without a leading slash because
 * `context.filename` is whatever ESLint was handed: the CLI passes an absolute path, while the
 * `Linter` of `packages/cli/src/webLintBoundaries.test.ts` passes the repository-relative one
 * verbatim (ESLint 10 `Linter#verify` does not resolve it). The directory itself must be an
 * exact segment: a file merely named like the bridge (`artifact-viewer-bridge-notes.ts`) or a
 * look-alike directory (`not-artifact-viewer-bridge/`) stays reportable, because a substring
 * match on the path would let any name containing the words opt out of the rule.
 */
function isAllowedBridge(filename) {
  if (!filename.includes('apps/web/src/app/')) {
    return false;
  }
  return filename.split('/').slice(0, -1).includes(ALLOWED_BRIDGE_DIRECTORY);
}

/**
 * The bypass method a member expression names, or null. A computed access is resolved only
 * for a string literal (`sanitizer['bypassSecurityTrustHtml']`); `sanitizer[name]` with a
 * variable property is the accepted limit, as in the sibling rules — such a property cannot
 * name one of the five methods without a literal somewhere else that this rule still sees.
 */
function memberBypassName(node) {
  if (!node.computed) {
    return node.property.type === 'Identifier' && BYPASS_NAME.test(node.property.name)
      ? node.property.name
      : null;
  }
  const property = node.property;
  if (property.type !== 'Literal' || typeof property.value !== 'string') {
    return null;
  }
  return BYPASS_NAME.test(property.value) ? property.value : null;
}

/** The bypass method a destructuring property binds, or null. */
function propertyBypassName(property) {
  if (property.type !== 'Property') {
    return null;
  }
  const key = property.key;
  if (property.computed) {
    return key.type === 'Literal' && typeof key.value === 'string' && BYPASS_NAME.test(key.value)
      ? key.value
      : null;
  }
  return key.type === 'Identifier' && BYPASS_NAME.test(key.name) ? key.name : null;
}

module.exports = {
  meta: {
    type: 'problem',
    docs: {
      description:
        'No DomSanitizer bypassSecurityTrust* outside the reviewed artifact-viewer bridge.',
    },
    schema: [],
    messages: {
      bypass:
        'Do not use DomSanitizer.{{name}}: bypassing Angular sanitization is forbidden outside the reviewed artifact-viewer bridge (00 §5.6 W6, FR-09-15). The app-origin Trusted Types policy also blocks it at run time, so the browser throws. Bind the text with interpolation or [innerText] instead.',
    },
  },

  create(context) {
    const filename = (context.filename || '').replaceAll('\\', '/');
    if (isAllowedBridge(filename)) {
      return {};
    }

    return {
      // Covers the call, the optional-chained call, the string-computed call and the
      // alias-building member access in one visitor: the member access is the bypass use,
      // whether or not a call follows it.
      MemberExpression(node) {
        const name = memberBypassName(node);
        if (name) {
          context.report({ node, messageId: 'bypass', data: { name } });
        }
      },

      // A bare identifier call only: the member form above already owns its own report, so
      // one site yields one message.
      CallExpression(node) {
        const callee = node.callee;
        if (callee.type === 'Identifier' && BYPASS_NAME.test(callee.name)) {
          context.report({ node, messageId: 'bypass', data: { name: callee.name } });
        }
      },

      ObjectPattern(node) {
        for (const property of node.properties) {
          const name = propertyBypassName(property);
          if (name) {
            context.report({ node: property, messageId: 'bypass', data: { name } });
          }
        }
      },
    };
  },
};
