'use strict';

// 09.1.1-T11.4 (AC9): the local `style-tokens` ESLint plugin.
//
// Two rules, one detector (`./raw-literal-detector.js`):
// - `no-raw-literals` lints `apps/web/**/*.css` through the @eslint/css language. The
//   contract is `docs/design/web/tokens.md` § "Lint rule `style-tokens`"; the only file
//   allowed to hold raw values is `apps/web/src/styles/tokens.css`, which the config
//   exempts.
// - `no-raw-literals-inline` lints the `styles: [...]` entries of an `@Component`, because
//   an Angular component style may be a string instead of a .css file. A template-literal
//   entry is scanned as a stylesheet AND each interpolation followed by a raw unit
//   (`${space}px`, `${duration}ms`) is reported, since the unit makes the value a literal
//   length or time. A `styles: [NAME]` reference to a module-level `const NAME = '...'` of
//   the same file is resolved and checked.
//
// NOT covered (documented limitation): template `style="..."` attributes and `[style.x]`
// bindings. They are template input rather than a stylesheet literal and Angular writes
// them through the sanitizer; the token contract is enforced for stylesheets only. Also not
// covered: a style string built elsewhere or imported, the `@core.Component({...})`
// namespace form (`@Component` is matched as a bare identifier), and `styles` inside a
// `*.spec.ts` file (the app config block ignores specs).

const {
  advanceLoc,
  findDeclarations,
  findRawLiterals,
  isDurationProperty,
  isLengthProperty,
} = require('./raw-literal-detector.js');

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
  rawDuration:
    "Raw duration '{{value}}' in a component style: use var(--eg-duration-fast) or var(--eg-duration-standard).",
};

/**
 * A raw unit directly after a template interpolation: `${space}px`, `${duration}ms`. The
 * interpolation holds a JavaScript number, so the unit turns it into a CSS length or time
 * even though no digit appears in the stylesheet text; scanning the text alone would never
 * see it. `ms` is listed before `s` so one match, not two, is produced for a time.
 */
const INTERPOLATED_UNIT_RE = /^(px|rem|em|ms|s)(?![a-zA-Z0-9-])/iu;

/** The `INTERPOLATED_UNIT_RE` units that build a length; the others build a duration. */
const INTERPOLATED_LENGTH_UNITS = new Set(['px', 'rem', 'em']);

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
        'Disallow raw colour, length and motion-duration literals in component styles; use the --eg-* design tokens.',
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

/**
 * Resolves a `styles: [NAME]` reference to the initializer of a module-level
 * `const NAME = <string>` of the SAME file. That is the one indirection still visible to a
 * single-file lint pass; anything else (an import, a function call, a `let` that can change)
 * is a documented limitation rather than a guess.
 *
 * @param {import('eslint').SourceCode} sourceCode the TypeScript source code
 * @param {object} identifier the referenced identifier node
 * @returns {object | undefined} the string or template literal initializer, when resolvable
 */
function resolveConstInitializer(sourceCode, identifier) {
  let variable;
  for (let scope = sourceCode.getScope(identifier); scope && !variable; scope = scope.upper) {
    variable = scope.set.get(identifier.name);
  }

  const definition = variable?.defs?.[0];
  if (!definition || definition.type !== 'Variable' || !definition.node?.init) {
    return undefined;
  }

  const declaration = definition.parent;
  if (!declaration || declaration.kind !== 'const' || declaration.parent?.type !== 'Program') {
    return undefined;
  }

  const initializer = definition.node.init;
  if (initializer.type === 'Literal' && typeof initializer.value === 'string') {
    return initializer;
  }
  return initializer.type === 'TemplateLiteral' ? initializer : undefined;
}

/** @type {import('eslint').Rule.RuleModule} */
const noRawLiteralsInline = {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Disallow raw colour, length and motion-duration literals in the inline `styles` of an Angular component.',
      recommended: false,
      url: 'docs/design/web/tokens.md#lint-rule-style-tokens',
    },
    messages: MESSAGES,
  },
  create(context) {
    const sourceCode = context.sourceCode;
    const text = sourceCode.text;

    /**
     * Reports `${space}px`, `${space}rem`, `${space}em`, `${duration}ms` and `${duration}s`:
     * an interpolation immediately followed by a raw unit builds a literal length or time at
     * runtime, which is exactly what the token contract forbids. The report covers the unit
     * and names the interpolation in `value`.
     *
     * Only a declaration whose static value would be checked qualifies (a length property, a
     * duration property or a custom property), so `width: ${n}px` is not reported for the
     * same reason `width: 8px` is not: sizing is deliberately outside the contract.
     *
     * @param {object} expression a template interpolation's ESTree expression node
     * @param {Array<{ property: string, valueStart: number, valueText: string }>} declarations
     *   the declarations of the same stylesheet content
     * @param {number} contentStart file offset of the content's first character
     * @returns {void}
     */
    function reportInterpolatedUnit(expression, declarations, contentStart) {
      if (!expression.range) {
        return;
      }

      // The interpolation's closing brace is the first `}` after the expression: a `}` inside
      // the expression itself (an object literal, a nested template) is part of its range.
      const closingBrace = text.indexOf('}', expression.range[1]);
      if (closingBrace === -1) {
        return;
      }

      const unitStart = closingBrace + 1;
      const unitMatch = INTERPOLATED_UNIT_RE.exec(text.slice(unitStart, unitStart + 3));
      if (!unitMatch) {
        return;
      }

      const unitOffset = unitStart - contentStart;
      const declaration = declarations.find(
        (candidate) =>
          unitOffset >= candidate.valueStart &&
          unitOffset < candidate.valueStart + candidate.valueText.length,
      );
      if (!declaration) {
        return;
      }
      const property = declaration.property.toLowerCase();
      const unit = unitMatch[1].toLowerCase();
      const isLengthUnit = INTERPOLATED_LENGTH_UNITS.has(unit);
      const checked =
        property.startsWith('--') ||
        (isLengthUnit ? isLengthProperty(property) : isDurationProperty(property));
      if (!checked) {
        return;
      }

      const interpolationStart = text.lastIndexOf('${', expression.range[0]);
      const interpolation =
        interpolationStart === -1 ? '' : text.slice(interpolationStart, unitStart);
      // `index` is an absolute offset into the file here, so `valueOffset` is 0.
      reportFindings(
        context,
        expression,
        text,
        0,
        [
          {
            index: unitStart,
            length: unitMatch[1].length,
            messageId: isLengthUnit ? 'rawLength' : 'rawDuration',
            data: { value: `${interpolation}${unitMatch[1]}` },
          },
        ],
        0,
      );
    }

    /**
     * Replaces every `${...}` of a template entry with spaces, keeping the text length so every
     * offset still maps to the file. A declaration value scanner stops at `{`/`}` — an
     * interpolation would truncate `padding: ${space}px` at the `${` — and expression source is
     * JavaScript, not stylesheet text, so it must not be scanned for literals either.
     *
     * @param {string} content the entry's source text
     * @param {number} contentStart file offset of `content[0]`
     * @param {Array<object>} expressions the template's interpolation nodes
     * @returns {string} same-length text with the interpolations blanked
     */
    function blankInterpolations(content, contentStart, expressions) {
      if (expressions.length === 0) {
        return content;
      }

      const characters = content.split('');
      const blank = (from, to) => {
        for (
          let offset = Math.max(from, 0);
          offset < to && offset < characters.length;
          offset += 1
        ) {
          characters[offset] = ' ';
        }
      };

      for (const expression of expressions) {
        if (!expression.range) {
          continue;
        }
        const open = text.lastIndexOf('${', expression.range[0]);
        if (open === -1) {
          continue;
        }
        const close = text.indexOf('}', expression.range[1]);
        blank(open - contentStart, close === -1 ? content.length : close + 1 - contentStart);
      }

      return characters.join('');
    }

    /**
     * Checks one `styles` entry (a string literal, or a template literal with or without
     * interpolation) as a stylesheet.
     *
     * @param {object} element literal or template-literal node
     * @returns {void}
     */
    function checkStylesEntry(element) {
      let contentStart;
      let content;
      let expressions = [];

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
        expressions = element.expressions;
        content = blankInterpolations(
          text.slice(contentStart, contentEnd),
          contentStart,
          expressions,
        );
      } else if (element.type === 'Literal' && typeof element.value === 'string') {
        // The source slice, not `element.value`: an escape sequence must not shift the
        // offsets the report locations are computed from. The content starts one character
        // (the opening quote) after the node start.
        contentStart = element.range[0] + 1;
        content = text.slice(contentStart, element.range[1] - 1);
      } else {
        return;
      }

      const declarations = findDeclarations(content);

      for (const declaration of declarations) {
        const findings = findRawLiterals(declaration.property, declaration.valueText);
        if (findings.length === 0) {
          continue;
        }
        reportFindings(context, element, text, contentStart + declaration.valueStart, findings, 0);
      }

      for (const expression of expressions) {
        reportInterpolatedUnit(expression, declarations, contentStart);
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
            if (!element) {
              continue;
            }
            if (element.type === 'Identifier') {
              const initializer = resolveConstInitializer(sourceCode, element);
              if (initializer) {
                checkStylesEntry(initializer);
              }
              continue;
            }
            checkStylesEntry(element);
          }
          return;
        }
        if (value.type === 'Identifier') {
          const initializer = resolveConstInitializer(sourceCode, value);
          if (initializer) {
            checkStylesEntry(initializer);
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
