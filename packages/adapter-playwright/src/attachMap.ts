// `result.attachments` → artifact kind, media type and the sensitive flag.
//
// Why the fixed table is the only source of a media type: `upload-url` names a kind and a
// media type, and the server accepts a closed list per kind
// (`contracts/evidence/artifact-kind-defaults.v1.json` — the same table
// `packages/cli/src/uploadPaths.ts` mirrors). An attachment's own `contentType` comes from the
// test process, so it is only *checked* against that table; a disagreement is a skip
// (`attachment_type_mismatch`), never a re-typed upload. An extension the table does not name is
// a skip too (`attachment_unmapped_type`), because a guessed kind would file wrong evidence under
// the wrong retention row.
//
// Nothing here reads, stats or copies a file: the mapping is pure and the byte cap needs the
// size the path guard (`attachPaths.ts`) reports. A body-only attachment (an inline buffer
// instead of a path) is skipped as `attachment_no_path`, because uploading it would mean
// buffering the whole attachment inside the test process.
//
// Messages carry the file's BASE name only, with control and format characters escaped, so a
// skip can never echo a home directory into a `run.log` warning and never emits a live escape
// (`status.ts` `printable` removes them on free text; here the name is evidence, so an escape
// keeps the tampering visible instead of hiding it).
import { extname } from 'node:path';

/** The kinds Playwright produces an attachment for. */
export type AttachmentKind = 'trace' | 'video' | 'screenshot';

/**
 * One `TestResult.attachments` entry, narrowed to what the mapping reads.
 *
 * `path` and `body` are typed `| undefined` on purpose: Playwright's own attachment objects
 * carry the property with an explicit `undefined` value, which `exactOptionalPropertyTypes`
 * would refuse from a bare optional property.
 */
export interface PlaywrightAttachment {
  readonly name: string;
  readonly path?: string | undefined;
  readonly body?: unknown;
  readonly contentType: string;
}

/**
 * Why an attachment is not uploaded. These are the skip codes a `run.log` warning carries;
 * `attachment_over_cap`, `attachment_path_refused` and `attachment_unreadable` are decided by
 * the size check and the path guard, not here.
 */
export type SkipCode =
  | 'attachment_unmapped_type'
  | 'attachment_no_path'
  | 'attachment_type_mismatch'
  | 'attachment_over_cap'
  | 'attachment_path_refused'
  | 'attachment_unreadable';

/** An attachment the reporter may upload. */
export interface MappedAttachment {
  readonly kind: AttachmentKind;
  /** From the fixed table, never from the attachment's own `contentType`. */
  readonly mediaType: string;
  /** The path exactly as Playwright reported it; the guard resolves it. */
  readonly path: string;
  /** The file's base name only, escaped and capped for a log line — never a directory. */
  readonly name: string;
  readonly sensitive: boolean;
}

export type MapOutcome =
  | { readonly ok: true; readonly attachment: MappedAttachment }
  | { readonly ok: false; readonly code: SkipCode; readonly message: string };

/** One table row: the extension, the kind and media type it decides, and the name prefix. */
interface KindRule {
  readonly kind: AttachmentKind;
  readonly mediaType: string;
  /** `trace*.zip` / `video*.webm|mp4`: the base name must start with this. */
  readonly prefix?: string | undefined;
}

/**
 * The mapping table. `trace*.zip → trace`, `video*.(webm|mp4) → video`,
 * `*.(png|jpeg|jpg) → screenshot` — the media types are the ones the static per-kind
 * defaults accept. `trace` and `video` also require the name prefix, so `report.zip` is an
 * unmapped type rather than a trace by extension alone.
 */
const KIND_RULES: Readonly<Record<string, KindRule>> = {
  '.zip': { kind: 'trace', mediaType: 'application/zip', prefix: 'trace' },
  '.webm': { kind: 'video', mediaType: 'video/webm', prefix: 'video' },
  '.mp4': { kind: 'video', mediaType: 'video/mp4', prefix: 'video' },
  '.png': { kind: 'screenshot', mediaType: 'image/png' },
  '.jpeg': { kind: 'screenshot', mediaType: 'image/jpeg' },
  '.jpg': { kind: 'screenshot', mediaType: 'image/jpeg' },
};

/** The most UTF-16 units of a base name a message or `MappedAttachment.name` carries. */
const NAME_MAX = 120;

/** Control and format characters, escaped rather than removed (`status.ts` `printable`). */
const UNSAFE_CHARS = /[\p{Cc}\p{Cf}]/gu;

/** The escapes a reader can undo; every other unsafe character becomes `\u{<hex>}`. */
const NAMED_ESCAPES: Readonly<Record<string, string>> = {
  '\n': '\\n',
  '\r': '\\r',
  '\t': '\\t',
};

/**
 * Maps one attachment onto an artifact kind, or reports why it must be skipped. Pure: no
 * file system access, no buffering, and the media type always comes from {@link KIND_RULES}.
 *
 * The extension is read from `path` (case-insensitively) and the attachment's
 * `contentType` must agree with it; a body-only attachment has no path and is skipped.
 */
export function mapAttachment(
  attachment: PlaywrightAttachment,
  opts: { sensitive: boolean },
): MapOutcome {
  const name = shownName(attachment);
  const path = typeof attachment.path === 'string' ? attachment.path : '';
  if (path.trim().length === 0) {
    return {
      ok: false,
      code: 'attachment_no_path',
      message: `attachment '${name}': no file path (a body-only attachment is never uploaded), skipped`,
    };
  }
  const base = baseName(path);
  const rule = KIND_RULES[extname(base).toLowerCase()];
  if (
    rule === undefined ||
    (rule.prefix !== undefined && !base.toLowerCase().startsWith(rule.prefix))
  ) {
    return {
      ok: false,
      code: 'attachment_unmapped_type',
      message: `attachment '${name}': attachment type not mapped to an artifact kind, skipped`,
    };
  }
  if (mediaTypeOf(attachment.contentType) !== rule.mediaType) {
    return {
      ok: false,
      code: 'attachment_type_mismatch',
      message: `attachment '${name}': content type does not match the file extension, skipped`,
    };
  }
  return {
    ok: true,
    attachment: {
      kind: rule.kind,
      mediaType: rule.mediaType,
      path,
      name,
      sensitive: opts.sensitive === true,
    },
  };
}

/**
 * True when `bytes` exceeds the static cap of `kind` (`true` also for a size that is not a
 * non-negative finite number, because such a size is not a size). A cap of `undefined`
 * means the project's policy row is the only cap, so nothing is refused locally — exactly
 * at the cap passes, the next byte fails.
 *
 * The caps themselves live in `packages/cli` (`artifactKindMaxBytes`): this package must
 * not keep a second copy that could drift from the server's contract.
 */
export function isOverCap(
  kind: AttachmentKind,
  bytes: number,
  kindMaxBytes: Readonly<Record<string, number | undefined>>,
): boolean {
  const cap = kindMaxBytes[kind];
  if (typeof cap !== 'number' || !Number.isFinite(cap)) {
    return false;
  }
  if (!Number.isFinite(bytes) || bytes < 0) {
    return true;
  }
  return bytes > cap;
}

/** The last segment of a path that may use either separator; `''` when there is none. */
function baseName(path: string): string {
  const parts = path.split(/[\\/]/u);
  return parts[parts.length - 1] ?? '';
}

/** The name a message shows: base name only, control characters escaped, capped. */
function shownName(attachment: PlaywrightAttachment): string {
  const fromPath = typeof attachment.path === 'string' ? baseName(attachment.path) : '';
  const fromName = typeof attachment.name === 'string' ? baseName(attachment.name) : '';
  const shown = escapeControls(fromPath.length > 0 ? fromPath : fromName);
  return cut(shown.length > 0 ? shown : 'unnamed', NAME_MAX);
}

/**
 * Every control or format character as visible text: the named escapes first, `\u{<hex>}`
 * otherwise, so a newline in a file name cannot split a log line and a BIDI override cannot
 * reorder one.
 */
function escapeControls(value: string): string {
  return value.replace(
    UNSAFE_CHARS,
    (char: string) => NAMED_ESCAPES[char] ?? `\\u{${(char.codePointAt(0) ?? 0).toString(16)}}`,
  );
}

/** At most `max` UTF-16 units, never ending on a lone high surrogate. */
function cut(value: string, max: number): string {
  if (value.length <= max) return value;
  const last = value.charCodeAt(max - 1);
  return value.slice(0, last >= 0xd800 && last <= 0xdbff ? max - 1 : max);
}

/** The lower-cased media type part of a `Content-Type` value, parameters removed. */
function mediaTypeOf(contentType: unknown): string {
  if (typeof contentType !== 'string') return '';
  const semicolon = contentType.indexOf(';');
  return (semicolon < 0 ? contentType : contentType.slice(0, semicolon)).trim().toLowerCase();
}
