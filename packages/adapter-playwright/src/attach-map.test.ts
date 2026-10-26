// 04.3.3-T2.1: the AC4 attachment table, the per-kind size-cap boundary, and the sensitive
// flag (plan §2 AC4/AC6/AC7, §9 `attach-map.test.ts`).
//
// The point of these cases is that a *guess* is never uploaded: an extension outside the
// table, a name that does not carry the `trace`/`video` prefix, and a `contentType` that
// disagrees with the extension all end as skips with a code the reporter turns into a
// `run.log` warning. The media type of a mapped attachment always comes from the table, so
// an attachment cannot talk the reporter into an upload type the server does not accept.
import { describe, expect, it } from 'vitest';

import {
  isOverCap,
  mapAttachment,
  type MappedAttachment,
  type PlaywrightAttachment,
  type SkipCode,
} from './attachMap.js';

const DIR = '/ci/workspace/test-results/example';

/** One attachment whose path sits under {@link DIR}, so a message can be checked for it. */
function attachment(name: string, file: string, contentType: string): PlaywrightAttachment {
  return { name, path: `${DIR}/${file}`, contentType };
}

function mapped(input: PlaywrightAttachment, sensitive = false): MappedAttachment {
  const outcome = mapAttachment(input, { sensitive });
  if (!outcome.ok) throw new Error(`expected a mapping, got ${outcome.code}: ${outcome.message}`);
  return outcome.attachment;
}

function skipped(input: PlaywrightAttachment): { code: SkipCode; message: string } {
  const outcome = mapAttachment(input, { sensitive: false });
  if (outcome.ok) throw new Error(`expected a skip, got kind ${outcome.attachment.kind}`);
  return { code: outcome.code, message: outcome.message };
}

describe('mapAttachment: AC4 kind table', () => {
  it('maps trace*.zip to trace with the table media type', () => {
    const path = `${DIR}/trace.zip`;
    expect(mapped(attachment('trace', 'trace.zip', 'application/zip'))).toEqual({
      kind: 'trace',
      mediaType: 'application/zip',
      path,
      name: 'trace.zip',
      sensitive: false,
    });
    expect(mapped(attachment('trace', 'trace-1.zip', 'application/zip')).kind).toBe('trace');
  });

  it('decides by the file name of path, not by the attachment name', () => {
    // `name` is 'report' and the path's base name is 'trace.zip': only the path decides.
    expect(mapped(attachment('report', 'trace.zip', 'application/zip'))).toMatchObject({
      kind: 'trace',
      path: `${DIR}/trace.zip`,
      name: 'trace.zip',
    });
    // And the other way round: a trace-looking `name` under a `video.webm` path is a video.
    expect(mapped(attachment('trace', 'video.webm', 'video/webm'))).toMatchObject({
      kind: 'video',
      name: 'video.webm',
    });
  });

  it('maps video*.webm to video', () => {
    const path = `${DIR}/video.webm`;
    expect(mapped(attachment('video', 'video.webm', 'video/webm'))).toEqual({
      kind: 'video',
      mediaType: 'video/webm',
      path,
      name: 'video.webm',
      sensitive: false,
    });
  });

  it('maps video*.mp4 to video', () => {
    expect(mapped(attachment('video', 'video-2.mp4', 'video/mp4'))).toMatchObject({
      kind: 'video',
      mediaType: 'video/mp4',
    });
  });

  it('reads the extension case-insensitively (.WEBM)', () => {
    expect(mapped(attachment('video', 'VIDEO-3.WEBM', 'video/webm'))).toMatchObject({
      kind: 'video',
      mediaType: 'video/webm',
      name: 'VIDEO-3.WEBM',
    });
    expect(mapped(attachment('trace', 'TRACE.ZIP', 'application/zip')).kind).toBe('trace');
  });

  it('maps *.png, *.jpeg and *.jpg to screenshot', () => {
    expect(mapped(attachment('screenshot', 'shot.png', 'image/png'))).toMatchObject({
      kind: 'screenshot',
      mediaType: 'image/png',
    });
    expect(mapped(attachment('screenshot', 'shot.jpeg', 'image/jpeg'))).toMatchObject({
      kind: 'screenshot',
      mediaType: 'image/jpeg',
    });
    expect(mapped(attachment('screenshot', 'shot.jpg', 'image/jpeg'))).toMatchObject({
      kind: 'screenshot',
      mediaType: 'image/jpeg',
    });
  });

  it('compares the media type case-insensitively and ignores parameters', () => {
    expect(mapped(attachment('video', 'video.webm', ' Video/WEBM ; codecs=vp9'))).toMatchObject({
      kind: 'video',
      mediaType: 'video/webm',
    });
    expect(mapped(attachment('screenshot', 'shot.png', 'IMAGE/PNG; foo=bar')).mediaType).toBe(
      'image/png',
    );
  });

  it('never takes the media type from the attachment', () => {
    expect(mapped(attachment('trace', 'trace.zip', 'application/zip; x=1')).mediaType).toBe(
      'application/zip',
    );
  });
});

describe('mapAttachment: skips', () => {
  it('skips a .zip whose name does not start with trace', () => {
    const { code, message } = skipped(attachment('report', 'other.zip', 'application/zip'));
    expect(code).toBe('attachment_unmapped_type');
    expect(message).toContain('other.zip');
    // The attachment name says 'trace'; the path's base name decides, so this stays a skip.
    expect(skipped(attachment('trace', 'other.zip', 'application/zip')).code).toBe(
      'attachment_unmapped_type',
    );
  });

  it('skips video extensions whose name does not start with video', () => {
    expect(skipped(attachment('clip', 'clip.webm', 'video/webm')).code).toBe(
      'attachment_unmapped_type',
    );
    expect(skipped(attachment('clip', 'clip.mp4', 'video/mp4')).code).toBe(
      'attachment_unmapped_type',
    );
  });

  it('skips an unknown extension with the unmapped-type code', () => {
    const { code, message } = skipped(attachment('result', 'result.json', 'application/json'));
    expect(code).toBe('attachment_unmapped_type');
    expect(message).toBe(
      "attachment 'result.json': attachment type not mapped to an artifact kind, skipped",
    );
  });

  it('skips a content type that disagrees with the extension', () => {
    expect(skipped(attachment('trace', 'trace.zip', 'text/html')).code).toBe(
      'attachment_type_mismatch',
    );
    expect(skipped(attachment('video', 'video.mp4', 'video/webm')).code).toBe(
      'attachment_type_mismatch',
    );
    expect(skipped(attachment('screenshot', 'shot.png', 'image/jpeg')).code).toBe(
      'attachment_type_mismatch',
    );
  });

  it('skips an empty content type as a mismatch', () => {
    expect(skipped(attachment('trace', 'trace.zip', '')).code).toBe('attachment_type_mismatch');
  });

  it('skips a body-only attachment without reading the body', () => {
    const body = Buffer.from('not read');
    const { code, message } = skipped({ name: 'trace', contentType: 'application/zip', body });
    expect(code).toBe('attachment_no_path');
    expect(message).toContain('trace');
    expect(body.toString('utf8')).toBe('not read');
  });

  it('skips an empty path as no path', () => {
    expect(skipped({ name: 'trace', path: '', contentType: 'application/zip' }).code).toBe(
      'attachment_no_path',
    );
    expect(skipped({ name: 'trace', path: '   ', contentType: 'application/zip' }).code).toBe(
      'attachment_no_path',
    );
  });

  it('keeps the directory out of every message', () => {
    const cases: PlaywrightAttachment[] = [
      attachment('report', 'other.zip', 'application/zip'),
      attachment('trace', 'trace.zip', 'text/html'),
      { name: 'trace', contentType: 'application/zip' },
    ];
    for (const input of cases) {
      const { message } = skipped(input);
      expect(message).not.toContain(DIR);
      expect(message).not.toContain('/ci/workspace');
      expect(message.length).toBeLessThanOrEqual(200);
    }
  });

  it('escapes control characters in the name instead of emitting them', () => {
    const { message } = skipped(attachment('trace', 'tr\nace.zip', 'text/html'));
    expect(message).toContain('tr\\nace.zip');
    expect(message).not.toMatch(/[\n\r]/u);
  });
});

describe('isOverCap', () => {
  const caps: Readonly<Record<string, number | undefined>> = {
    trace: 1000,
    video: undefined,
    screenshot: 10,
  };

  it('passes a file exactly at the cap', () => {
    expect(isOverCap('trace', 1000, caps)).toBe(false);
    expect(isOverCap('screenshot', 10, caps)).toBe(false);
  });

  it('fails one byte over the cap', () => {
    expect(isOverCap('trace', 1001, caps)).toBe(true);
    expect(isOverCap('screenshot', 11, caps)).toBe(true);
  });

  it('never fails a kind whose cap is undefined', () => {
    expect(isOverCap('video', 10_000_000_000, caps)).toBe(false);
    expect(isOverCap('trace', Number.MAX_SAFE_INTEGER, { trace: undefined })).toBe(false);
  });

  it('treats a size that is not a non-negative number as over the cap', () => {
    expect(isOverCap('trace', Number.NaN, caps)).toBe(true);
    expect(isOverCap('trace', -1, caps)).toBe(true);
  });
});

describe('mapAttachment: sensitive flag', () => {
  it('copies the sensitive flag through', () => {
    expect(mapped(attachment('trace', 'trace.zip', 'application/zip'), true).sensitive).toBe(true);
    expect(mapped(attachment('trace', 'trace.zip', 'application/zip'), false).sensitive).toBe(
      false,
    );
  });
});
