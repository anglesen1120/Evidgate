'use strict';

// 09.1.1-T11.4 (AC9): the local `style-tokens` ESLint plugin.
//
// Two rules, one detector (`./raw-literal-detector.js`):
// - `no-raw-literals` lints `apps/web/**/*.css` through the @eslint/css language. The
//   contract is `docs/design/web/tokens.md` § "Lint rule `style-tokens`"; the only file
//   allowed to hold raw values is `apps/web/src/styles/tokens.css`, which the config
//   exempts.
// - `no-raw-literals-inline` lints the `styles: [...]` entries of an `@Component`, because
//   an Angular component style may be a string instead of a .css file.
//
// NOT covered (documented limitation): template `style="..."` attributes and `[style.x]`
// bindings. They are template input rather than a stylesheet literal and Angular writes
// them through the sanitizer; the token contract is enforced for stylesheets only.

const { advanceLoc, findDeclarations, findRawLiterals } = require('./raw-literal-detector.js');

/**
 * The 1-based `line`/`column` of an absolute character offset of a source text.
 *
 * Report locations are computed from offsets and the text itself, never from a node's
 * `loc.column`: ESTree columns are 0-based while @eslint/css reports 1-based ones, and a
 * template literal's quasi `range` may or may not include its backticks. Ranges are exact
 * in both ASTs, so offsets are the only reliable input.
 *
 * @param {string} sourceText the whole file text
 * @param {number} offset absolute character offset into `sourceText`
 * @returns {{ line: number, column: number }} 1-based location
 */
function locFromOffset(sourceText, offset) {
  return advanceLoc(sourceText, { line: 1, column: 1 }, offset);
}

const MESSAGES = {
  rawHexColour:
    "Raw colour '{{value}}' in a component style: use an --eg-* token (tokens.css is the only file allowed raw values).",
  rawColourFunction:
    "Raw colour function '{{value}}' in a component style: use an --eg-* token (tokens.css is the only file allowed raw values).",
  rawNamedColour:
    "Raw named colour '{{value}}' in a component style: use an --eg-* token (tokens.css is the only file allowed raw values).",
  rawLength:
    "Raw length '{{value}}' in a component style: use an --eg-* spacing, radius, border-width or type token.",
};

/**
 * Reports every finding of one scanned value text, translating the detector's character
 * offsets into report locations.
 *
 * @param {import('eslint').Rule.RuleContext} context rule context
 * @param {object} reportNode AST node the reports are anchored to
 * @param {string} sourceText the whole file text
 * @param {number} valueOffset absolute offset of the scanned text's first character
 * @param {Array<{ index: number, length: number, messageId: string, data: object }>} findings
 * @param {1 | 0} columnStart the linted language's column origin: @eslint/css reports
 *   1-based columns, JavaScript/TypeScript 0-based ones, and ESLint adds its own offset
 *   from `language.columnStart` when it turns `loc` into a message
 * @returns {void}
 */
function reportFindings(context, reportNode, sourceText, valueOffset, findings, columnStart) {
  const toLoc = (offset) => {
    const location = locFromOffset(sourceText, offset);
    return columnStart === 1 ? location : { line: location.line, column: location.column - 1 };
  };

  for (const finding of findings) {
    const start = valueOffset + finding.index;
    context.report({
      node: reportNode,
      loc: {
        start: toLoc(start),
        end: toLoc(start + finding.length),
      },
      messageId: finding.messageId,
      data: finding.data,
    });
  }
}

/**
 * Whether a declaration is part of an at-rule PRELUDE (`@supports (display: grid)`,
 * `@container style(--x: red)`). Preludes are never checked: CSS cannot read a custom
 * property in a media/container/supports condition, so the documented literal
 * `--eg-bp-tablet`/`--eg-bp-desktop` mirrors are the only possible spelling there.
 *
 * @param {import('eslint').SourceCode} sourceCode the CSS source code
 * @param {object} node the Declaration node
 * @returns {boolean} true when the declaration is a prelude declaration
 */
function isPreludeDeclaration(sourceCode, node) {
  return sourceCode
    .getAncestors(node)
    .some((ancestor) => ancestor.type === 'AtrulePrelude' || ancestor.type === 'SupportsCondition');
}

/** @type {import('eslint').Rule.RuleModule} */
const noRawLiterals = {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Disallow raw colour and length literals in component styles; use the --eg-* design tokens.',
      recommended: false,
      url: 'docs/design/web/tokens.md#lint-rule-style-tokens',
    },
    languages: ['css/css'],
    messages: MESSAGES,
  },
  create(context) {
    const sourceCode = context.sourceCode;
    const text = sourceCode.text;

    return {
      Declaration(node) {
        if (isPreludeDeclaration(sourceCode, node)) {
          return;
        }

        const value = node.value;
        if (!value || !value.loc) {
          return;
        }

        const valueStart = value.loc.start.offset;
        const valueText = text.slice(valueStart, value.loc.end.offset);
        const findings = findRawLiterals(node.property, valueText);
        if (findings.length === 0) {
          return;
        }

        reportFindings(context, node, text, valueStart, findings, 1);
      },
    };
  },
};

/**
 * The `styles` object property of a `@Component({ ... })` metadata object.
 *
 * @param {object} objectExpression the decorator metadata object expression
 * @returns {object | undefined} the `styles` property node
 */
function findStylesProperty(objectExpression) {
  for (const property of objectExpression.properties) {
    if (property.type !== 'Property') {
      continue;
    }
    const key = property.key;
    const name =
      key.type === 'Identifier' ? key.name : key.type === 'Literal' ? key.value : undefined;
    if (name === 'styles') {
      return property;
    }
  }
  return undefined;
}

/** @type {import('eslint').Rule.RuleModule} */
const noRawLiteralsInline = {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Disallow raw colour and length literals in the inline `styles` of an Angular component.',
      recommended: false,
      url: 'docs/design/web/tokens.md#lint-rule-style-tokens',
    },
    messages: MESSAGES,
  },
  create(context) {
    const sourceCode = context.sourceCode;
    const text = sourceCode.text;

    /**
     * Checks one `styles` entry (a string literal or a template literal without
     * interpolation) as a stylesheet.
     *
     * @param {object} element literal or template-literal node
     * @returns {void}
     */
    function checkStylesEntry(element) {
      let contentStart;
      let content;

      if (element.type === 'TemplateLiteral') {
        const first = element.quasis[0];
        const last = element.quasis[element.quasis.length - 1];
        if (!first || !last) {
          return;
        }
        // A quasi range includes its opening/closing backtick in @typescript-eslint's AST;
        // test the characters instead of assuming either shape.
        contentStart = text[first.range[0]] === '`' ? first.range[0] + 1 : first.range[0];
        const contentEnd = text[last.range[1] - 1] === '`' ? last.range[1] - 1 : last.range[1];
        content = text.slice(contentStart, contentEnd);
      } else if (element.type === 'Literal' && typeof element.value === 'string') {
        // The source slice, not `element.value`: an escape sequence must not shift the
        // offsets the report locations are computed from. The content starts one character
        // (the opening quote) after the node start.
        contentStart = element.range[0] + 1;
        content = text.slice(contentStart, element.range[1] - 1);
      } else {
        return;
      }

      for (const declaration of findDeclarations(content)) {
        const findings = findRawLiterals(declaration.property, declaration.valueText);
        if (findings.length === 0) {
          continue;
        }
        reportFindings(context, element, text, contentStart + declaration.valueStart, findings, 0);
      }
    }

    return {
      CallExpression(node) {
        if (node.callee.type !== 'Identifier' || node.callee.name !== 'Component') {
          return;
        }
        const metadata = node.arguments[0];
        if (!metadata || metadata.type !== 'ObjectExpression') {
          return;
        }
        const stylesProperty = findStylesProperty(metadata);
        if (!stylesProperty) {
          return;
        }

        const value = stylesProperty.value;
        if (value.type === 'ArrayExpression') {
          for (const element of value.elements) {
            if (element) {
              checkStylesEntry(element);
            }
          }
          return;
        }
        checkStylesEntry(value);
      },
    };
  },
};

module.exports = {
  rules: {
    'no-raw-literals': noRawLiterals,
    'no-raw-literals-inline': noRawLiteralsInline,
  },
};
