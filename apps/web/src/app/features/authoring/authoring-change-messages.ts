// The reason strings of the pending-changes queue, keyed by the `details[].reason` the queue, list
// and apply routes send. They live here rather than in the Angular template catalog because the
// contract names them `authoring.reason.<reason>` and `scripts/check-i18n-keys.mjs` only accepts
// `@@area.name` ids without underscores, so a template unit could not carry the agreed key.
//
// Nothing renders these yet: the pending-changes screen is its own story, and shipping the copy
// first keeps a reason from reaching that screen with only the English envelope summary.

/** The two locales the web app ships (00 §5.6 W8). */
export type AuthoringReasonLocale = 'vi' | 'en';

/** One vi + en line per reason, so a missing language is a type error rather than a blank cell. */
export const AUTHORING_REASON_TEXT: Readonly<
  Record<string, Readonly<Record<AuthoringReasonLocale, string>>>
> = {
  payload_invalid: { vi: 'nội dung thay đổi không hợp lệ', en: 'the change payload is invalid' },
  target_deleted: { vi: 'mục đã bị xoá', en: 'the item was deleted' },
  merge_sources_min: { vi: 'cần ít nhất 2 mục để gộp', en: 'at least 2 items are needed to merge' },
  merge_sources_max: {
    vi: 'tối đa 50 mục mỗi lần gộp',
    en: 'at most 50 items can be merged at once',
  },
  survivor_not_in_sources: {
    vi: 'mục giữ lại phải nằm trong danh sách gộp',
    en: 'the surviving item must be one of the merged items',
  },
  split_items_min: { vi: 'cần 2–20 mục khi tách', en: '2–20 items are needed to split' },
  split_items_max: { vi: 'cần 2–20 mục khi tách', en: '2–20 items are needed to split' },
  comment_body_invalid: {
    vi: 'bình luận phải có 1–2000 ký tự',
    en: 'a comment needs 1–2000 characters',
  },
  too_many_changes: {
    vi: 'tối đa 200 thay đổi mỗi lần áp dụng',
    en: 'at most 200 changes per apply',
  },
  ids_with_filters: {
    vi: 'không kết hợp lựa chọn với bộ lọc khác',
    en: 'the selection cannot be combined with other filters',
  },
  required: { vi: 'thiếu điều kiện phiên bản', en: 'the version precondition is missing' },
  change_already_pending: {
    vi: 'mục này đã có một thay đổi cấu trúc đang chờ',
    en: 'this item already has a structural change pending',
  },
  change_not_pending: {
    vi: 'thay đổi này không còn ở trạng thái chờ',
    en: 'this change is no longer pending',
  },
  item_has_cases: {
    vi: 'Mục này còn nội dung bên trong',
    en: 'this entry still has content inside',
  },
  batch_stale: { vi: 'danh sách đã cũ', en: 'the selection is stale' },
  target_changed: { vi: 'mục đã thay đổi', en: 'the item changed' },
};

/** The catalogue key of a reason, spelled `authoring.reason.<reason>` as the contract names it. */
export function authoringReasonKey(reason: string): string {
  return `authoring.reason.${reason}`;
}

/**
 * The copy of one `details[].reason` in the given locale, or `undefined` for a reason this
 * catalogue does not know — the caller then falls back to the envelope's own summary rather than
 * inventing text.
 */
export function authoringReasonText(
  reason: string | null | undefined,
  locale: AuthoringReasonLocale,
): string | undefined {
  if (reason === null || reason === undefined) return undefined;
  return Object.hasOwn(AUTHORING_REASON_TEXT, reason)
    ? AUTHORING_REASON_TEXT[reason]?.[locale]
    : undefined;
}
