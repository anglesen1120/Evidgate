// The reason strings of the requirement intake, keyed by the machine code the server sends: the
// `details[].reason` of a refused intake request and the `failure_code` of a rejected extraction.
// The server never sends localized prose; this catalogue is the only place the words live. It sits
// beside the pending-changes catalogue for the same reason: `scripts/check-i18n-keys.mjs` accepts
// only `@@area.name` template ids without underscores, so the agreed key cannot be a template unit.
//
// No line names an HTTP status or echoes any document content.

/** The two locales the web app ships (00 §5.6 W8). */
export type AuthoringIntakeLocale = 'vi' | 'en';

/** One vi + en line per code, so a missing language is a type error rather than a blank cell. */
export const AUTHORING_INTAKE_TEXT: Readonly<
  Record<string, Readonly<Record<AuthoringIntakeLocale, string>>>
> = {
  // Request refusals (`details[].reason`).
  empty_input: {
    vi: 'Chưa có nội dung yêu cầu để xử lý',
    en: 'There is no requirement content to process',
  },
  media_type_not_allowed: {
    vi: 'Định dạng tệp này không được hỗ trợ',
    en: 'This file type is not supported',
  },
  size_limit_exceeded: {
    vi: 'Nội dung vượt quá giới hạn kích thước cho phép',
    en: 'The content is larger than the allowed size',
  },
  invalid_text: {
    vi: 'Văn bản chứa ký tự không hợp lệ',
    en: 'The text contains characters that are not allowed',
  },
  secret_value_detected: {
    vi: 'Văn bản có vẻ chứa thông tin bí mật; hãy xoá trước khi gửi',
    en: 'The text appears to contain a secret; remove it before sending',
  },
  // Extraction failures (`failure_code` of a rejected intake).
  container_mismatch: {
    vi: 'Nội dung tệp không khớp với định dạng đã khai báo',
    en: 'The file content does not match its declared type',
  },
  macro_present: {
    vi: 'Tài liệu chứa macro nên không thể xử lý',
    en: 'The document contains macros and cannot be processed',
  },
  encryption_unsupported: {
    vi: 'Tài liệu được mã hoá hoặc đặt mật khẩu nên không thể xử lý',
    en: 'The document is encrypted or password-protected and cannot be processed',
  },
  external_reference_present: {
    vi: 'Tài liệu tham chiếu tới nội dung bên ngoài nên không thể xử lý',
    en: 'The document links to external content and cannot be processed',
  },
  archive_limit_exceeded: {
    vi: 'Cấu trúc nén của tài liệu vượt quá giới hạn cho phép',
    en: 'The document’s internal structure exceeds the allowed limits',
  },
  page_limit_exceeded: {
    vi: 'Tài liệu có quá nhiều trang',
    en: 'The document has too many pages',
  },
  char_limit_exceeded: {
    vi: 'Văn bản trích xuất vượt quá số ký tự cho phép',
    en: 'The extracted text exceeds the allowed length',
  },
  no_extractable_text: {
    vi: 'Không tìm thấy văn bản trong tài liệu',
    en: 'No text could be found in the document',
  },
  extraction_timeout: {
    vi: 'Việc trích xuất văn bản mất quá nhiều thời gian',
    en: 'Extracting the text took too long',
  },
  extraction_failed: {
    vi: 'Không thể trích xuất văn bản từ tài liệu',
    en: 'The text could not be extracted from the document',
  },
};

/** The catalogue key of a code, spelled `authoring.intake.reason.<code>`. */
export function authoringIntakeKey(code: string): string {
  return `authoring.intake.reason.${code}`;
}

/**
 * The copy of one intake code in the given locale, or `undefined` for a code this catalogue does
 * not know — the caller then falls back to the envelope's own summary rather than inventing text.
 */
export function authoringIntakeText(
  code: string | null | undefined,
  locale: AuthoringIntakeLocale,
): string | undefined {
  if (code === null || code === undefined) return undefined;
  return Object.hasOwn(AUTHORING_INTAKE_TEXT, code)
    ? AUTHORING_INTAKE_TEXT[code]?.[locale]
    : undefined;
}
