// The sixteen pending-changes reason strings must exist in BOTH languages, and the key set is the
// one the contract can send — no more, no fewer. These ids cannot be Angular template units
// (`scripts/check-i18n-keys.mjs` rejects the underscores in `authoring.reason.<reason>`), so this
// spec is the guard instead of the catalog parity check.
import {
  AUTHORING_REASON_TEXT,
  authoringReasonKey,
  authoringReasonText,
  type AuthoringReasonLocale,
} from './authoring-change-messages';

const SIXTEEN = [
  'payload_invalid',
  'target_deleted',
  'merge_sources_min',
  'merge_sources_max',
  'survivor_not_in_sources',
  'split_items_min',
  'split_items_max',
  'comment_body_invalid',
  'too_many_changes',
  'ids_with_filters',
  'required',
  'change_already_pending',
  'change_not_pending',
  'item_has_cases',
  'batch_stale',
  'target_changed',
];

const LOCALES: readonly AuthoringReasonLocale[] = ['vi', 'en'];

describe('pending-changes reason catalogue', () => {
  it('holds exactly the sixteen reasons the contract can send', () => {
    expect(Object.keys(AUTHORING_REASON_TEXT).sort()).toEqual([...SIXTEEN].sort());
  });

  it('spells every catalogue key authoring.reason.<reason>', () => {
    expect(
      Object.keys(AUTHORING_REASON_TEXT)
        .map((reason) => authoringReasonKey(reason))
        .sort(),
    ).toEqual(SIXTEEN.map((reason) => `authoring.reason.${reason}`).sort());
  });

  it.each(LOCALES)('has a non-empty %s line for every reason', (locale) => {
    const blank = SIXTEEN.filter((reason) => !AUTHORING_REASON_TEXT[reason]?.[locale]?.trim());
    expect(blank).toEqual([]);
  });

  it('keeps the vi line distinct from the English source', () => {
    const same = SIXTEEN.filter(
      (reason) => AUTHORING_REASON_TEXT[reason]?.vi === AUTHORING_REASON_TEXT[reason]?.en,
    );
    expect(same).toEqual([]);
  });

  it('resolves a known reason in both locales and leaves an unknown one to the envelope summary', () => {
    expect(authoringReasonText('batch_stale', 'en')).toBe('the selection is stale');
    expect(authoringReasonText('batch_stale', 'vi')).toBe('danh sách đã cũ');
    expect(authoringReasonText('item_has_cases', 'vi')).toBe('Mục này còn nội dung bên trong');
    expect(authoringReasonText('not_a_reason', 'en')).toBeUndefined();
    expect(authoringReasonText(null, 'en')).toBeUndefined();
    expect(authoringReasonText(undefined, 'vi')).toBeUndefined();
    // `toString` and friends are inherited members, never a reason.
    expect(authoringReasonText('toString', 'en')).toBeUndefined();
    expect(authoringReasonText('constructor', 'vi')).toBeUndefined();
  });
});
