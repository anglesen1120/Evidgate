#!/usr/bin/env node
// 09.1.1-T6.1 — AC6: the key-completeness check exits 1 on a missing key in either locale.
//
// `ng extract-i18n` writes the source catalog (XLIFF 1.2, custom ids `@@area.name`), and every
// locale catalog must cover every id in it with a non-empty <target> and the same placeholder
// set. This script is that gate. It is dependency-free (node: fs/path/url only) so
// `npm ci --ignore-scripts` on CI needs nothing extra, and it reads XLIFF with a small
// hand-written linear scanner instead of an XML library.
//
// Fail-closed on purpose: a missing source catalog, a source catalog with zero <trans-unit>, or
// an angular.json without `locales` is an error, because an empty check must never pass. A
// malformed unit (self-closing, unclosed, or without a closing ">") is an error for the same
// reason: it can never be matched against a locale.
//
// Every path is resolved against the angular.json directory, so the result does not depend on
// the caller's cwd. CLI:
//   node scripts/check-i18n-keys.mjs [--angular-json <path>] [--root <path>]
import { existsSync, readFileSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

/** The `ng extract-i18n` output, relative to the angular.json directory (`root`). */
export const SOURCE_CATALOG = 'src/locale/messages.xlf';
/** The 09.1.1-T6.1 custom-id convention every catalog id must follow. */
const ID_CONVENTION = '^[a-z][a-z0-9]*(\\.[a-z][a-z0-9-]*)+$';
const ID_PATTERN = new RegExp(ID_CONVENTION);
const DEFAULT_ANGULAR_JSON = resolve(fileURLToPath(new URL('../angular.json', import.meta.url)));
const USAGE = 'usage: node scripts/check-i18n-keys.mjs [--angular-json <path>] [--root <path>]';
/** Stands in for `<x …/>` inside segment text: a placeholder is content, never blank. */
const PLACEHOLDER_TOKEN = '\uFFFC';
const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };

/** The value of `name` on a tag, quoted with either `"` or `'`. */
function attribute(tag, name) {
  const match = new RegExp(`\\b${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`, 'i').exec(tag);
  return match?.[1] ?? match?.[2];
}

/** The five XML entities, decoded in one pass so `&amp;lt;` stays `&lt;`. */
function decodeEntities(text) {
  return text.replace(/&(amp|lt|gt|quot|apos);/g, (_match, name) => ENTITIES[name]);
}

/**
 * One `<source>`/`<target>` body: `<x id="…"/>` placeholders become opaque tokens and are
 * recorded by id; every other element is markup and contributes only its text.
 */
function parseSegmentText(raw) {
  const placeholders = [];
  const withTokens = raw.replace(/<\/?[a-zA-Z][^>]*>/g, (tag) => {
    if (!/^<x\b/.test(tag)) return '';
    const id = attribute(tag, 'id');
    if (id !== undefined) placeholders.push(id);
    return PLACEHOLDER_TOKEN;
  });
  return { text: decodeEntities(withTokens), placeholders };
}

const UNIT_OPEN = '<trans-unit';
const UNIT_CLOSE = '</trans-unit>';
const ALT_TRANS_OPEN = '<alt-trans';
const ALT_TRANS_CLOSE = '</alt-trans>';

/**
 * One `<trans-unit>` body with every `<alt-trans>` block removed: an alternative translation
 * carries its own `<target>`, which must never stand in for the unit's real one. An unclosed
 * `<alt-trans>` swallows the rest of the body, so its `<target>` cannot pass as the real one.
 */
function withoutAltTrans(body) {
  const kept = [];
  let cursor = 0;
  for (;;) {
    const open = body.indexOf(ALT_TRANS_OPEN, cursor);
    if (open === -1) {
      kept.push(body.slice(cursor));
      return kept.join('');
    }
    const close = body.indexOf(ALT_TRANS_CLOSE, open);
    if (close === -1) {
      kept.push(body.slice(cursor, open));
      return kept.join('');
    }
    kept.push(body.slice(cursor, open));
    cursor = close + ALT_TRANS_CLOSE.length;
  }
}

/**
 * `<trans-unit>` bodies of one catalog, in document order; malformed units are appended to
 * `errors` against `label`.
 *
 * Linear on purpose: a `[\s\S]*?` regex rescans to the end of the file for every unclosed
 * `<trans-unit>`, which is quadratic, so the scan is `indexOf` from the current cursor and never
 * revisits text. A self-closing `<trans-unit …/>` declares no key and is reported and skipped
 * without swallowing the unit that follows it; an unclosed or truncated one is reported and ends
 * the scan, because a broken catalog must fail closed instead of silently under-counting keys.
 */
function parseCatalog(content, label, errors) {
  const units = [];
  let cursor = 0;
  for (;;) {
    const open = content.indexOf(UNIT_OPEN, cursor);
    if (open === -1) return units;
    const tagEnd = content.indexOf('>', open);
    const header = tagEnd === -1 ? '' : content.slice(open + UNIT_OPEN.length, tagEnd);
    // `<trans-unitfoo>` is a different element, which is what the old regex `\b` rejected.
    if (/^[A-Za-z0-9_]/.test(header)) {
      cursor = open + UNIT_OPEN.length;
      continue;
    }
    const id = attribute(header, 'id');
    const named = id === undefined ? UNIT_OPEN : `${UNIT_OPEN} id="${id}"`;
    if (tagEnd === -1) {
      errors.push(`${label}: ${named} is truncated — it has no closing ">"`);
      return units;
    }
    if (/\/\s*$/.test(header)) {
      errors.push(
        `${label}: ${named}/> is self-closing — a unit needs an explicit <source> and ` +
          '</trans-unit>, so its key was not checked',
      );
      cursor = tagEnd + 1;
      continue;
    }
    const close = content.indexOf(UNIT_CLOSE, tagEnd + 1);
    if (close === -1) {
      errors.push(
        `${label}: ${named}> is never closed — missing </trans-unit>; ` +
          'stopped checking this file',
      );
      return units;
    }
    const body = withoutAltTrans(content.slice(tagEnd + 1, close));
    const segment = (name) => {
      const found = new RegExp(`<${name}\\b[^>]*>([\\s\\S]*?)<\\/${name}>`).exec(body);
      return found ? parseSegmentText(found[1]) : null;
    };
    units.push({ id, source: segment('source'), target: segment('target') });
    cursor = close + UNIT_CLOSE.length;
  }
}

/** A segment holds no content when it is absent, or only whitespace and no placeholder. */
function isBlank(segment) {
  if (segment === null) return true;
  const content = segment.text.split(PLACEHOLDER_TOKEN).join('').trim();
  return content === '' && segment.placeholders.length === 0;
}

/** The custom-id convention is declared as `@@area.name`; the `@@` itself is not in the file. */
function bareId(id) {
  return id.startsWith('@@') ? id.slice(2) : id;
}

/** Duplicate and badly formatted ids of one catalog; returns the ids it declares. */
function checkIds(units, label, errors) {
  const ids = new Set();
  for (const unit of units) {
    if (unit.id === undefined) {
      errors.push(`${label}: <trans-unit> without an id attribute`);
      continue;
    }
    if (ids.has(unit.id)) {
      errors.push(`${label}: duplicate id "${unit.id}"`);
      continue;
    }
    ids.add(unit.id);
    if (!ID_PATTERN.test(bareId(unit.id))) {
      errors.push(
        `${label}: id "${unit.id}" does not follow the @@area.name convention (${ID_CONVENTION})`,
      );
    }
  }
  return ids;
}

/** `locales.<name>.translation`, which Angular also accepts as an array of files. */
function translationFiles(entry) {
  const value = entry?.translation;
  if (typeof value === 'string') return [value];
  if (Array.isArray(value)) return value.filter((path) => typeof path === 'string');
  return [];
}

function keyList(placeholders) {
  const unique = [...new Set(placeholders)].sort();
  return unique.length === 0 ? '(none)' : unique.join(', ');
}

/**
 * Checks every configured locale catalog against the source catalog.
 * Pure apart from reading the files it is pointed at: it never throws, it reports.
 */
export function checkI18nKeys({ angularJsonPath, root } = {}) {
  const errors = [];
  const locales = [];
  let keyCount = 0;

  const angularPath = angularJsonPath
    ? resolve(root ?? process.cwd(), angularJsonPath)
    : DEFAULT_ANGULAR_JSON;
  const baseDir = root ? resolve(root) : dirname(angularPath);
  const shown = (file) => {
    const path = relative(baseDir, file);
    return path.startsWith('..') || path === '' ? file : path;
  };

  if (!existsSync(angularPath)) {
    errors.push(`${angularPath}: does not exist`);
    return { errors, locales, keyCount };
  }
  let config;
  try {
    config = JSON.parse(readFileSync(angularPath, 'utf8'));
  } catch (error) {
    errors.push(`${shown(angularPath)}: is not valid JSON (${error.message})`);
    return { errors, locales, keyCount };
  }
  const i18n = config?.projects?.web?.i18n;
  if (!i18n || typeof i18n !== 'object') {
    errors.push(
      `${shown(angularPath)}: projects.web.i18n is missing — this check needs ` +
        'sourceLocale and locales',
    );
    return { errors, locales, keyCount };
  }
  const localeEntries =
    i18n.locales && typeof i18n.locales === 'object' ? Object.entries(i18n.locales) : [];
  if (localeEntries.length === 0) {
    errors.push(
      `${shown(angularPath)}: projects.web.i18n.locales is missing — declare at least one ` +
        `translation locale (sourceLocale "${i18n.sourceLocale ?? 'unset'}")`,
    );
    return { errors, locales, keyCount };
  }
  locales.push(...localeEntries.map(([locale]) => locale));

  const sourcePath = resolve(baseDir, SOURCE_CATALOG);
  if (!existsSync(sourcePath)) {
    errors.push(
      `source catalog ${shown(sourcePath)} does not exist — run \`ng extract-i18n\` first`,
    );
    return { errors, locales, keyCount };
  }
  const sourceUnits = parseCatalog(readFileSync(sourcePath, 'utf8'), shown(sourcePath), errors);
  if (sourceUnits.length === 0) {
    errors.push(
      `source catalog ${shown(sourcePath)} has no <trans-unit> entries — ` +
        'an empty check must never pass',
    );
    return { errors, locales, keyCount };
  }
  keyCount = sourceUnits.length;
  checkIds(sourceUnits, shown(sourcePath), errors);
  const sourceById = new Map(
    sourceUnits.filter((unit) => unit.id !== undefined).map((unit) => [unit.id, unit]),
  );

  for (const [locale, entry] of localeEntries) {
    const files = translationFiles(entry).map((path) => resolve(baseDir, path));
    if (files.length === 0) {
      errors.push(`locale "${locale}": projects.web.i18n.locales.${locale}.translation is missing`);
      continue;
    }
    const localeHead = `locale "${locale}" (${files.map(shown).join(', ')})`;
    const presentIds = new Set();
    let readFiles = 0;

    for (const file of files) {
      if (!existsSync(file)) {
        errors.push(`translation file ${shown(file)} (locale "${locale}") does not exist`);
        continue;
      }
      readFiles += 1;
      const label = shown(file);
      const units = parseCatalog(readFileSync(file, 'utf8'), label, errors);
      checkIds(units, label, errors);

      for (const unit of units) {
        if (unit.id === undefined) continue;
        presentIds.add(unit.id);
        const sourceUnit = sourceById.get(unit.id);
        if (!sourceUnit) {
          errors.push(`${label}: id "${unit.id}" is not in the source catalog (stale key)`);
          continue;
        }
        if (isBlank(unit.target)) {
          const state = unit.target === null ? 'has no <target> element' : 'has an empty <target>';
          errors.push(`${label}: id "${unit.id}" ${state}`);
          continue;
        }
        const expected = keyList(sourceUnit.source?.placeholders ?? []);
        const actual = keyList(unit.target.placeholders);
        if (expected !== actual) {
          errors.push(
            `${label}: id "${unit.id}" placeholder sets differ from the source ` +
              `(source: ${expected}; target: ${actual})`,
          );
        }
      }
    }

    if (readFiles === 0) continue;
    for (const unit of sourceUnits) {
      if (unit.id === undefined || presentIds.has(unit.id)) continue;
      errors.push(`${localeHead}: missing id "${unit.id}" present in the source catalog`);
    }
  }

  return { errors, locales, keyCount };
}

function parseArguments(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--help' || argument === '-h') {
      return { help: true };
    }
    const name =
      argument === '--angular-json'
        ? 'angularJsonPath'
        : argument === '--root'
          ? 'root'
          : undefined;
    if (name === undefined) {
      throw new Error(`unknown argument "${argument}"`);
    }
    const value = argv[index + 1];
    if (value === undefined || value.startsWith('--')) {
      throw new Error(`${argument} needs a value`);
    }
    options[name] = value;
    index += 1;
  }
  return { options };
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  let parsed;
  try {
    parsed = parseArguments(process.argv.slice(2));
  } catch (error) {
    console.error(`i18n keys: ${error.message}`);
    console.error(USAGE);
    process.exitCode = 2;
  }
  if (parsed?.help === true) {
    console.log(USAGE);
  } else if (parsed !== undefined) {
    const { errors, locales, keyCount } = checkI18nKeys(parsed.options);
    for (const error of errors) {
      console.error(`i18n keys: ${error}`);
    }
    const summary = `${keyCount} source key(s), ${locales.length} locale(s): ${locales.join(', ')}`;
    if (errors.length > 0) {
      console.log(`i18n keys: ${summary} — ${errors.length} error(s).`);
      // `exitCode`, not `exit()`: stdout/stderr writes to a pipe must keep draining.
      process.exitCode = 1;
    } else {
      console.log(`i18n keys: ${summary} — every catalog is complete.`);
    }
  }
}
