'use strict';

// 09.1.1-T11.4 (AC9): the ONE detector behind both `style-tokens` rules.
//
// `style-tokens/no-raw-literals` (apps/web/**/*.css) reads a parsed declaration from the
// @eslint/css AST and hands its property name and value text here. `style-tokens/
// no-raw-literals-inline` (the `styles: [...]` of an `@Component`) first scans a whole
// stylesheet string for `property: value` pairs (`findDeclarations`) and then calls the
// same function per pair. Splitting detection into two implementations is exactly how the
// two rules would drift; the only difference between them is where the property name and
// the value text come from.
//
// Scope of the check (the contract is `docs/design/web/tokens.md`, section
// "Lint rule `style-tokens`"):
// - raw hex colours and colour functions in ANY declaration value, including a custom
//   property and a `var(--x, <fallback>)` fallback (the fallback is ordinary value text);
// - CSS named colours in colour-bearing properties and in custom properties;
// - non-zero `px`/`rem`/`em` lengths in spacing, radius, border-width, type-size and
//   shadow-offset properties, and in EVERY custom property (a local `--local-gap: 8px`
//   copies a raw length exactly like `gap: 8px` does) — the categories that have numeric
//   tokens.
// Allowed everywhere: `var(--*)` references, `0`, unitless numbers, percentages, and the
// keywords below. `@media`/`@container`/`@supports` PRELUDES are never scanned — CSS cannot
// read custom properties there, so `--eg-bp-tablet`/`--eg-bp-desktop` have literal mirrors.

/** CSS Color Module Level 4 named colours: a named colour is a raw literal like any other. */
const NAMED_COLOURS = new Set(
  [
    'aliceblue',
    'antiquewhite',
    'aqua',
    'aquamarine',
    'azure',
    'beige',
    'bisque',
    'black',
    'blanchedalmond',
    'blue',
    'blueviolet',
    'brown',
    'burlywood',
    'cadetblue',
    'chartreuse',
    'chocolate',
    'coral',
    'cornflowerblue',
    'cornsilk',
    'crimson',
    'cyan',
    'darkblue',
    'darkcyan',
    'darkgoldenrod',
    'darkgray',
    'darkgreen',
    'darkgrey',
    'darkkhaki',
    'darkmagenta',
    'darkolivegreen',
    'darkorange',
    'darkorchid',
    'darkred',
    'darksalmon',
    'darkseagreen',
    'darkslateblue',
    'darkslategray',
    'darkslategrey',
    'darkturquoise',
    'darkviolet',
    'deeppink',
    'deepskyblue',
    'dimgray',
    'dimgrey',
    'dodgerblue',
    'firebrick',
    'floralwhite',
    'forestgreen',
    'fuchsia',
    'gainsboro',
    'ghostwhite',
    'gold',
    'goldenrod',
    'gray',
    'green',
    'greenyellow',
    'grey',
    'honeydew',
    'hotpink',
    'indianred',
    'indigo',
    'ivory',
    'khaki',
    'lavender',
    'lavenderblush',
    'lawngreen',
    'lemonchiffon',
    'lightblue',
    'lightcoral',
    'lightcyan',
    'lightgoldenrodyellow',
    'lightgray',
    'lightgreen',
    'lightgrey',
    'lightpink',
    'lightsalmon',
    'lightseagreen',
    'lightskyblue',
    'lightslategray',
    'lightslategrey',
    'lightsteelblue',
    'lightyellow',
    'lime',
    'limegreen',
    'linen',
    'magenta',
    'maroon',
    'mediumaquamarine',
    'mediumblue',
    'mediumorchid',
    'mediumpurple',
    'mediumseagreen',
    'mediumslateblue',
    'mediumspringgreen',
    'mediumturquoise',
    'mediumvioletred',
    'midnightblue',
    'mintcream',
    'mistyrose',
    'moccasin',
    'navajowhite',
    'navy',
    'oldlace',
    'olive',
    'olivedrab',
    'orange',
    'orangered',
    'orchid',
    'palegoldenrod',
    'palegreen',
    'paleturquoise',
    'palevioletred',
    'papayawhip',
    'peachpuff',
    'peru',
    'pink',
    'plum',
    'powderblue',
    'purple',
    'rebeccapurple',
    'red',
    'rosybrown',
    'royalblue',
    'saddlebrown',
    'salmon',
    'sandybrown',
    'seagreen',
    'seashell',
    'sienna',
    'silver',
    'skyblue',
    'slateblue',
    'slategray',
    'slategrey',
    'snow',
    'springgreen',
    'steelblue',
    'tan',
    'teal',
    'thistle',
    'tomato',
    'turquoise',
    'violet',
    'wheat',
    'white',
    'whitesmoke',
    'yellow',
    'yellowgreen',
  ].map((name) => name.toLowerCase()),
);

/**
 * Value keywords that are not literals: they inherit a token-resolved value, are theme
 * keywords, or are the explicit "no colour"/"no effect" cases. `none` covers
 * `box-shadow: none` and `background: none`; `auto` covers `margin: auto`.
 */
const ALLOWED_KEYWORDS = new Set([
  'transparent',
  'currentcolor',
  'inherit',
  'initial',
  'unset',
  'revert',
  'revert-layer',
  'none',
  'auto',
  'normal',
]);

/** Function names that can only produce a raw colour. `color-mix(` is not one of them. */
const COLOUR_FUNCTION_RE = /(?:^|[^a-zA-Z0-9_-])(rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\(/giu;

/** 3, 4, 6 and 8 digit hex colours; longer alternatives first so `#aabbcc` is not read as `#aabb`. */
const HEX_COLOUR_RE =
  /(?<![a-zA-Z0-9_-])#(?:[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{4}|[0-9a-f]{3})(?![a-zA-Z0-9_-])/giu;

/** A non-zero absolute length in a token-backed unit. A unitless `0` never matches. */
const DIMENSION_RE = /(?<![a-zA-Z0-9_.-])-?(?:\d+(?:\.\d+)?|\.\d+)(?:px|rem|em)(?![a-zA-Z0-9-])/giu;

/** The bare number of a dimension match, for the `0px`/`0rem` exemption. */
const DIMENSION_NUMBER_RE = /^-?(?:\d+(?:\.\d+)?|\.\d+)/u;

/** An identifier candidate for the named-colour check. */
const IDENTIFIER_RE = /(?<![a-zA-Z0-9_-])([a-zA-Z][a-zA-Z-]*)(?![a-zA-Z0-9_-])/gu;

/** `border-radius` and friends carry lengths and `calc()`, never a colour name. */
const RADIUS_PROPERTY_RE = /(^|-)radius$/u;

/** `border-width`/`outline-width` are numeric too: `--eg-border-width`/`--eg-focus-width` exist. */
const LENGTH_PROPERTY_PATTERNS = [
  /^margin(-.+)?$/u,
  /^padding(-.+)?$/u,
  /^gap$/u,
  /^row-gap$/u,
  /^column-gap$/u,
  /^grid-(row|column)-gap$/u,
  // `scroll-margin*`/`scroll-padding*` are a spacing scale like `margin`/`padding`, longhands
  // and logical forms included: a scroll snap offset is never viewport-relative.
  /^scroll-margin(-.+)?$/u,
  /^scroll-padding(-.+)?$/u,
  /^inset(-.+)?$/u,
  /^(top|right|bottom|left)$/u,
  /^border-radius$/u,
  /^border-(top|bottom)-(left|right)-radius$/u,
  /^border-(start|end)-(start|end)-radius$/u,
  /^border-width$/u,
  /^border-(top|right|bottom|left|block|inline)(-(start|end))?-width$/u,
  /^border$/u,
  /^border-(top|right|bottom|left|block|inline)(-(start|end))?$/u,
  /^outline$/u,
  /^outline-width$/u,
  /^outline-offset$/u,
  /^font$/u,
  /^font-size$/u,
  /^line-height$/u,
  /^box-shadow$/u,
  /^text-shadow$/u,
];

/**
 * Property names whose value can carry a colour. Named colours are only checked here
 * (and in custom properties), because `content: red`-style keywords elsewhere are not
 * colours.
 *
 * @param {string} property lower-cased property name
 * @returns {boolean} whether a named colour in this value is a raw literal
 */
function isColourProperty(property) {
  if (RADIUS_PROPERTY_RE.test(property)) {
    return false;
  }

  return (
    property === 'color' ||
    /^background(-.+)?$/u.test(property) ||
    /^border(-.+)?$/u.test(property) ||
    /^outline(-.+)?$/u.test(property) ||
    /^column-rule(-.+)?$/u.test(property) ||
    /^text-decoration(-.+)?$/u.test(property) ||
    /^text-emphasis(-.+)?$/u.test(property) ||
    property === 'fill' ||
    property === 'stroke' ||
    /^stroke-.+$/u.test(property) ||
    property === 'box-shadow' ||
    property === 'text-shadow' ||
    property === 'caret-color' ||
    property === 'accent-color' ||
    property === 'scrollbar-color' ||
    property === 'filter'
  );
}

/**
 * Property names whose value is a length in a token-backed scale (spacing, radius,
 * border width, type size/leading, shadow offsets).
 *
 * Deliberately excluded, because no token covers them and a literal is the honest value:
 * `width`/`height`/`min-*`/`max-*` and inset-free sizing, `letter-spacing`, `transform`
 * offsets, `translate`, and the transition/animation durations.
 *
 * @param {string} property lower-cased property name
 * @returns {boolean} whether a `px`/`rem`/`em` literal in this value is a raw literal
 */
function isLengthProperty(property) {
  return LENGTH_PROPERTY_PATTERNS.some((pattern) => pattern.test(property));
}

/**
 * Blanks out the spans of a declaration value that cannot contain a colour or length:
 * comments, quoted strings (a font family or `content` string) and `url(...)` payloads
 * (an SVG fragment id is not a hex colour). Every blanked character becomes a space, so an
 * index into the returned string is still an index into the original value.
 *
 * The mask is length-preserving in UTF-16 CODE UNITS, not code points: every index a caller
 * uses (`match.index`, `masked[index]`, `valueText.slice(...)`) is a code-unit offset, so the
 * text is split with `split('')`. Splitting with `[...text]` would collapse a surrogate pair
 * into one entry and shift every offset after a non-BMP character (an emoji) by one.
 *
 * @param {string} text declaration value text
 * @returns {string} same-length text with non-code spans blanked
 */
function maskNonCode(text) {
  const characters = text.split('');
  const length = text.length;

  const blank = (from, to) => {
    for (let index = from; index < to && index < length; index += 1) {
      if (text[index] !== '\n' && text[index] !== '\r') {
        characters[index] = ' ';
      }
    }
  };

  let index = 0;
  while (index < length) {
    const character = text[index];

    if (character === '/' && text[index + 1] === '*') {
      const close = text.indexOf('*/', index + 2);
      const stop = close === -1 ? length : close + 2;
      blank(index, stop);
      index = stop;
      continue;
    }

    if (character === '"' || character === "'") {
      let cursor = index + 1;
      while (cursor < length) {
        if (text[cursor] === '\\') {
          cursor += 2;
          continue;
        }
        if (text[cursor] === character) {
          cursor += 1;
          break;
        }
        cursor += 1;
      }
      blank(index, cursor);
      index = cursor;
      continue;
    }

    if (text.slice(index, index + 4).toLowerCase() === 'url(') {
      const close = text.indexOf(')', index + 4);
      const stop = close === -1 ? length : close + 1;
      blank(index, stop);
      index = stop;
      continue;
    }

    index += 1;
  }

  return characters.join('');
}

/**
 * Advances a 1-based `line`/`column` location by `offset` characters of `text`.
 *
 * @param {string} text the scanned text
 * @param {{ line: number, column: number }} start 1-based location of `text[0]`
 * @param {number} offset character offset into `text`
 * @returns {{ line: number, column: number }} 1-based location of `text[offset]`
 */
function advanceLoc(text, start, offset) {
  let line = start.line;
  let column = start.column;
  const limit = Math.min(offset, text.length);

  for (let index = 0; index < limit; index += 1) {
    const character = text[index];
    if (character === '\n' || character === '\f') {
      line += 1;
      column = 1;
    } else if (character === '\r') {
      if (text[index + 1] !== '\n') {
        line += 1;
        column = 1;
      }
    } else {
      column += 1;
    }
  }

  return { line, column };
}

/**
 * Finds the raw colour and raw length literals of one declaration value.
 *
 * Findings carry `index`/`length` offsets into `valueText`; the caller turns those into
 * locations because only it knows where the value starts in the file.
 *
 * @param {string} property property name (`--custom` included)
 * @param {string} valueText the declaration's value text, as written
 * @returns {Array<{ index: number, length: number, messageId: string, data: { value: string } }>}
 *   one entry per raw literal, in source order
 */
function findRawLiterals(property, valueText) {
  if (typeof property !== 'string' || typeof valueText !== 'string' || valueText === '') {
    return [];
  }

  const normalisedProperty = property.toLowerCase();
  const masked = maskNonCode(valueText);
  const findings = [];

  for (const match of masked.matchAll(HEX_COLOUR_RE)) {
    findings.push({
      index: match.index,
      length: match[0].length,
      messageId: 'rawHexColour',
      data: { value: valueText.slice(match.index, match.index + match[0].length) },
    });
  }

  for (const match of masked.matchAll(COLOUR_FUNCTION_RE)) {
    const nameIndex = match.index + match[0].length - match[1].length - 1;
    findings.push({
      index: nameIndex,
      length: match[1].length + 1,
      messageId: 'rawColourFunction',
      data: { value: `${match[1].toLowerCase()}(` },
    });
  }

  if (isColourProperty(normalisedProperty) || normalisedProperty.startsWith('--')) {
    for (const match of masked.matchAll(IDENTIFIER_RE)) {
      const identifier = match[1];
      const identifierIndex = match.index;
      const lower = identifier.toLowerCase();
      const followedByParenthesis = masked[identifierIndex + identifier.length] === '(';
      if (followedByParenthesis || ALLOWED_KEYWORDS.has(lower) || !NAMED_COLOURS.has(lower)) {
        continue;
      }
      findings.push({
        index: identifierIndex,
        length: identifier.length,
        messageId: 'rawNamedColour',
        data: { value: valueText.slice(identifierIndex, identifierIndex + identifier.length) },
      });
    }
  }

  // A custom property is checked for lengths as well as colours: `--local-gap: 8px` copies a
  // raw value just as `gap: 8px` does, and a component that declares it locally is exactly how
  // a raw length would otherwise slip past the rule. `tokens.css` stays exempt at config level.
  if (isLengthProperty(normalisedProperty) || normalisedProperty.startsWith('--')) {
    for (const match of masked.matchAll(DIMENSION_RE)) {
      const numberMatch = DIMENSION_NUMBER_RE.exec(match[0]);
      if (numberMatch && Number.parseFloat(numberMatch[0]) === 0) {
        continue;
      }
      findings.push({
        index: match.index,
        length: match[0].length,
        messageId: 'rawLength',
        data: { value: valueText.slice(match.index, match.index + match[0].length) },
      });
    }
  }

  return findings.sort((left, right) => left.index - right.index);
}

/**
 * A `property: value` declaration of a stylesheet held in a string. The prefix guard is
 * what keeps `@media (min-width: 768px)` preludes out: a declaration may only start at the
 * beginning of the text or after `{`, `}` or `;`, and an at-rule prelude follows `@`.
 */
const DECLARATION_RE =
  /(?:^|[;{}])(\s*)(--[a-zA-Z][a-zA-Z0-9-]*|[a-zA-Z][a-zA-Z0-9-]*)\s*:([^;{}]*)/gu;

/**
 * Finds the declarations of a stylesheet string, skipping at-rule preludes and selectors.
 *
 * @param {string} stylesheet CSS text, e.g. the content of a `styles: [...]` entry
 * @returns {Array<{ property: string, valueStart: number, valueText: string }>} declarations
 */
function findDeclarations(stylesheet) {
  if (typeof stylesheet !== 'string' || stylesheet === '') {
    return [];
  }

  const masked = maskNonCode(stylesheet);
  const declarations = [];

  for (const match of masked.matchAll(DECLARATION_RE)) {
    const valueText = match[3] ?? '';
    declarations.push({
      property: match[2] ?? '',
      valueStart: match.index + match[0].length - valueText.length,
      valueText,
    });
  }

  return declarations;
}

module.exports = {
  ALLOWED_KEYWORDS,
  NAMED_COLOURS,
  advanceLoc,
  findDeclarations,
  findRawLiterals,
  isColourProperty,
  isLengthProperty,
  maskNonCode,
};
