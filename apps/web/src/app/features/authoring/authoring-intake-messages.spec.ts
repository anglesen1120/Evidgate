// Every code the intake can send must exist in BOTH languages, and the key set is the one the
// contract names — the request reasons of `POST …/authoring/intakes` and the ten `failure_code`
// values of a rejected extraction — no more, no fewer. The ids carry underscores, so they cannot be
// Angular template units; this spec is the guard instead of the catalog parity check.
import {
  AUTHORING_INTAKE_TEXT,
  authoringIntakeKey,
  authoringIntakeText,
  type AuthoringIntakeLocale,
} from './authoring-intake-messages';

const REQUEST_REASONS = [
  'empty_input',
  'media_type_not_allowed',
  'size_limit_exceeded',
  'invalid_text',
  'secret_value_detected',
];

const FAILURE_CODES = [
  'container_mismatch',
  'macro_present',
  'encryption_unsupported',
  'external_reference_present',
  'archive_limit_exceeded',
  'page_limit_exceeded',
  'char_limit_exceeded',
  'no_extractable_text',
  'extraction_timeout',
  'extraction_failed',
];

const ALL = [...REQUEST_REASONS, ...FAILURE_CODES];
const LOCALES: readonly AuthoringIntakeLocale[] = ['vi', 'en'];

describe('intake reason catalogue', () => {
  it('holds exactly the five request reasons and ten failure codes the contract can send', () => {
    expect(Object.keys(AUTHORING_INTAKE_TEXT).sort()).toEqual([...ALL].sort());
    expect(new Set(ALL).size).toBe(15);
  });

  it('spells every catalogue key authoring.intake.reason.<code>', () => {
    expect(
      Object.keys(AUTHORING_INTAKE_TEXT)
        .map((code) => authoringIntakeKey(code))
        .sort(),
    ).toEqual(ALL.map((code) => `authoring.intake.reason.${code}`).sort());
  });

  it.each(LOCALES)('has a non-empty %s line for every code', (locale) => {
    const blank = ALL.filter((code) => !AUTHORING_INTAKE_TEXT[code]?.[locale]?.trim());
    expect(blank).toEqual([]);
  });

  it('keeps the vi line distinct from the English source and every line distinct per locale', () => {
    const same = ALL.filter(
      (code) => AUTHORING_INTAKE_TEXT[code]?.vi === AUTHORING_INTAKE_TEXT[code]?.en,
    );
    expect(same).toEqual([]);
    for (const locale of LOCALES) {
      const lines = ALL.map((code) => AUTHORING_INTAKE_TEXT[code]?.[locale]);
      expect(new Set(lines).size).toBe(ALL.length);
    }
  });

  it('never names an HTTP status in any line', () => {
    const named = ALL.filter((code) =>
      LOCALES.some((locale) => /\b[45]\d\d\b/.test(AUTHORING_INTAKE_TEXT[code]?.[locale] ?? '')),
    );
    expect(named).toEqual([]);
  });

  it('resolves a known code in both locales and leaves an unknown one to the envelope summary', () => {
    expect(authoringIntakeText('no_extractable_text', 'en')).toBe(
      'No text could be found in the document',
    );
    expect(authoringIntakeText('no_extractable_text', 'vi')).toBe(
      'Không tìm thấy văn bản trong tài liệu',
    );
    expect(authoringIntakeText('not_a_reason', 'en')).toBeUndefined();
    expect(authoringIntakeText(null, 'en')).toBeUndefined();
    expect(authoringIntakeText(undefined, 'vi')).toBeUndefined();
    // `toString` and friends are inherited members, never a code.
    expect(authoringIntakeText('toString', 'en')).toBeUndefined();
    expect(authoringIntakeText('constructor', 'vi')).toBeUndefined();
  });
});
