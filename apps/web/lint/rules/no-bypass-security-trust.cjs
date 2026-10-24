// 09.1.1-T10 (AC7, ADR-0008 rule 4; 00 §5.6 W6, FR-09-15): the application origin runs under
// the nonce CSP + Trusted Types policy of apps/web/scripts/app-origin-csp.json, whose
// `trusted-types` list is deliberately `angular` only — no `angular#unsafe-bypass`. A
// `bypassSecurityTrust*` value therefore has no policy that would turn it into a Trusted value:
// the app-origin policy refuses the string when it reaches a DOM sink in the browser, and the
// failure surfaces at render time, far from the call that caused it. This rule reports the
// bypass at lint time instead, where the author still sees the line to replace.
//
// Every syntactic form that reaches a bypass method is reported, once per occurrence:
//   - a call through a member, `sanitizer.bypassSecurityTrustHtml(html)`, including optional
//     chaining (`sanitizer?.bypassSecurityTrustHtml(html)`) and a computed access with a
//     constant key (`sanitizer['bypassSecurityTrustHtml'](html)`, a template literal key, and
//     `sanitizer['bypass' + 'SecurityTrustHtml'](html)`);
//   - a member used as a value, which is how an alias is built:
//     `const bind = sanitizer.bypassSecurityTrustHtml;`
//   - a bare identifier call (`bypassSecurityTrustHtml(html)`), e.g. the local binding of a
//     destructuring;
//   - a destructuring of the member: `const { bypassSecurityTrustHtml } = sanitizer;`
//   - an import of the name, aliased or not:
//     `import { bypassSecurityTrustHtml as bind } from './sanitize';`
//   - any other constant that spells one of the five names: `const k = 'bypassSecurityTrustHtml';
//     sanitizer[k](html)`, `Reflect.get(sanitizer, 'bypassSecurityTrustHtml')`, an interpolated
//     template literal used as a key. A key this rule cannot evaluate has to hold the name as a
//     constant somewhere, and that constant is what the catch-all visitors report;
//   - a bypass call written inside an inline `template:` string, which no AST visitor can look
//     into: `<div [innerHTML]="sanitizer.bypassSecurityTrustHtml(html)"></div>`. That value is
//     scanned by content. A `templateUrl` file is outside this rule's scope: the Angular
//     template rules that do run on `.html` do not look for a bypass call.
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

/** The application root, as the sequence of path segments it must appear as. */
const APP_ROOT_SEGMENTS = ['apps', 'web', 'src', 'app'];

/** The DomSanitizer methods that switch Angular's sanitization off. */
const BYPASS_NAME = /^bypassSecurityTrust(Html|Style|Script|Url|ResourceUrl)$/;

/**
 * A bypass call written inside an inline Angular template, which is source text rather than
 * AST. The word boundary keeps a look-alike (`myBypassSecurityTrustHtml(`,
 * `bypassSecurityTrustHtmlX(`) out, and the `(` keeps a mere mention of the name out.
 */
const INLINE_TEMPLATE_CALL = /\b(bypassSecurityTrust(?:Html|Style|Script|Url|ResourceUrl))\s*\(/;

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
      // `apps/web/src/app`, which is the only prefix that can opt out of this rule.
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
 * Whether a file may use a bypass.
 *
 * `context.filename` is whatever ESLint was handed: the CLI passes an absolute path, while the
 * `Linter` of `packages/cli/src/webLintBoundaries.test.ts` passes the repository-relative one
 * verbatim (ESLint 10 `Linter#verify` does not resolve it). Only the segments below the last
 * `apps/web/src/app` marker are inspected, and only as whole segments: a checkout whose own
 * parent directory is named like the bridge, a `..` that leaves the bridge, a file merely named
 * like it (`artifact-viewer-bridge-notes.ts`) or a look-alike directory
 * (`not-artifact-viewer-bridge/`) must all stay reportable, which only exact segment equality
 * gets right.
 */
function isAllowedBridge(filename) {
  const segments = pathSegments(filename);
  const root = lastApplicationRoot(segments);
  if (root < 0) {
    return false;
  }
  // The last segment is the file itself; only the directories that hold it decide the answer.
  const directories = segments.slice(root + APP_ROOT_SEGMENTS.length, -1);
  return directories.includes(ALLOWED_BRIDGE_DIRECTORY);
}

/**
 * The string a constant expression stands for, or null when it is not constant: a string
 * literal, a template literal without expressions, or a `+` chain of those
 * (`'bypass' + 'SecurityTrustHtml'`). A template WITH expressions is not folded; its inner
 * literals are reported on their own instead, which is the case `sanitizer[`${name}`]` needs.
 */
function constantString(node) {
  if (node.type === 'Literal') {
    return typeof node.value === 'string' ? node.value : null;
  }
  if (node.type === 'TemplateLiteral' && node.expressions.length === 0) {
    return node.quasis[0].value.cooked ?? null;
  }
  if (node.type === 'BinaryExpression' && node.operator === '+') {
    const left = constantString(node.left);
    if (left === null) {
      return null;
    }
    const right = constantString(node.right);
    return right === null ? null : left + right;
  }
  return null;
}

/** The string a constant expression spells when it is one of the five methods, or null. */
function bypassMethodName(node) {
  const value = constantString(node);
  return value !== null && BYPASS_NAME.test(value) ? value : null;
}

/**
 * The bypass method a member expression names, or null. A computed access is resolved only for
 * a constant key; `sanitizer[key]` with a variable is left to the catch-all visitors below,
 * which report the constant that spells the name.
 */
function memberBypassName(node) {
  if (!node.computed) {
    return node.property.type === 'Identifier' && BYPASS_NAME.test(node.property.name)
      ? node.property.name
      : null;
  }
  return bypassMethodName(node.property);
}

/** The bypass method a destructuring property binds, or null. */
function propertyBypassName(property) {
  if (property.type !== 'Property') {
    return null;
  }
  if (property.computed) {
    return bypassMethodName(property.key);
  }
  return property.key.type === 'Identifier' && BYPASS_NAME.test(property.key.name)
    ? property.key.name
    : null;
}

/**
 * Whether a dedicated visitor already reports `node`, so the catch-all visitors do not report
 * the same site a second time: the computed property of a member expression, the computed key
 * of a destructuring property, and an imported name.
 */
function ownedByDedicatedVisitor(node) {
  const parent = node.parent;
  if (!parent) {
    return false;
  }
  if (parent.type === 'MemberExpression' && parent.computed && parent.property === node) {
    return true;
  }
  if (parent.type === 'ImportSpecifier' && parent.imported === node) {
    return true;
  }
  if (parent.type !== 'Property' || !parent.computed || parent.key !== node) {
    return false;
  }
  // Only destructuring: a computed key of an object literal is not a bypass use by itself.
  const owner = parent.parent;
  return owner !== undefined && owner.type === 'ObjectPattern';
}

/** The top of the `+` chain `node` belongs to, so one concatenation yields one report. */
function outerConcatenation(node) {
  let current = node;
  while (current.parent?.type === 'BinaryExpression' && current.parent.operator === '+') {
    current = current.parent;
  }
  return current;
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
        'Do not use DomSanitizer.{{name}}: bypassing Angular sanitization is forbidden outside the reviewed artifact-viewer bridge (00 §5.6 W6, FR-09-15). The app-origin policy has no `angular#unsafe-bypass` Trusted Types policy, so the browser refuses the value when it reaches a DOM sink. Bind the text with interpolation or [innerText] instead.',
    },
  },

  create(context) {
    if (isAllowedBridge(context.filename || '')) {
      return {};
    }

    /**
     * Reports the outermost constant of a chain that spells a bypass name, unless a dedicated
     * visitor owns that position already (`sanitizer['bypassSecurityTrustHtml']`). One site
     * yields one message: the member visitor owns member keys, and this owns every other
     * constant, which is where a name travels through a variable, `Reflect.get` or a template.
     */
    const reportConstant = (node) => {
      if (outerConcatenation(node) !== node) {
        return;
      }
      const name = bypassMethodName(node);
      if (name === null || ownedByDedicatedVisitor(node)) {
        return;
      }
      context.report({ node, messageId: 'bypass', data: { name } });
    };

    return {
      // Covers the call, the optional-chained call, the constant-key call and the
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

      // An inline Angular template is source text, not AST, so a bypass call inside it passes
      // every visitor above: `<div [innerHTML]="sanitizer.bypassSecurityTrustHtml(html)">`.
      // The property value is scanned by content instead. One report per property, even when
      // the template calls the method twice.
      Property(node) {
        const key = node.key;
        // Angular's `template` holds template source; a computed key is not that property.
        const isTemplate =
          !node.computed &&
          ((key.type === 'Identifier' && key.name === 'template') ||
            (key.type === 'Literal' && key.value === 'template'));
        if (!isTemplate) {
          return;
        }
        const template = constantString(node.value);
        const match = template === null ? null : INLINE_TEMPLATE_CALL.exec(template);
        if (match) {
          context.report({ node: node.value, messageId: 'bypass', data: { name: match[1] } });
        }
      },

      // The name imported under an alias is a bypass no member visitor can see: the alias call
      // is a bare identifier the rule cannot connect to DomSanitizer. The imported name is an
      // Identifier, or a string for an arbitrary module namespace name.
      ImportSpecifier(node) {
        const imported = node.imported;
        const name =
          imported.type === 'Identifier' && BYPASS_NAME.test(imported.name)
            ? imported.name
            : bypassMethodName(imported);
        if (name !== null) {
          context.report({ node, messageId: 'bypass', data: { name } });
        }
      },

      // The catch-all: a key built from a variable, `Reflect.get`, or a template cannot name a
      // bypass method without spelling it as a constant somewhere, and this reports that
      // constant.
      Literal: reportConstant,
      TemplateLiteral: reportConstant,
      BinaryExpression: reportConstant,
    };
  },
};
