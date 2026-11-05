// Mapping boundaries the delivered specs leave open: base-name truncation that must never
// split a surrogate, escaping of control and bidi characters, and content-type mismatches.
import { describe, expect, it } from 'vitest';

import { mapAttachment } from './attachMap.js';

describe('QA probe: AC4 mapping boundaries', () => {
  it('bounds a very long base name and never splits a surrogate', () => {
    const longName = `${'a'.repeat(5000)}.png`;
    const outcome = mapAttachment(
      { name: longName, path: `/root/${longName}`, body: undefined, contentType: 'image/png' },
      { sensitive: false },
    );
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.attachment.name.length).toBeLessThanOrEqual(120);
    const last = outcome.attachment.name.charCodeAt(outcome.attachment.name.length - 1);
    expect(last >= 0xd800 && last <= 0xdbff).toBe(false);
    expect(outcome.attachment.name).not.toContain('/root');

    // A cut that would land inside an astral character: 119 ASCII + one surrogate pair.
    const astral = `${'a'.repeat(119)}😀.png`;
    const cut = mapAttachment(
      { name: astral, path: `/root/${astral}`, body: undefined, contentType: 'image/png' },
      { sensitive: false },
    );
    expect(cut.ok).toBe(true);
    if (!cut.ok) return;
    const cutLast = cut.attachment.name.charCodeAt(cut.attachment.name.length - 1);
    expect(cutLast >= 0xd800 && cutLast <= 0xdbff).toBe(false);
  });

  it('escapes control and bidi characters in a record name and in a skip message', () => {
    const name = 'shot\u0007\n\u202eevil.png';
    const mapped = mapAttachment(
      { name, path: `/root/${name}`, body: undefined, contentType: 'image/png' },
      { sensitive: false },
    );
    expect(mapped.ok).toBe(true);
    if (!mapped.ok) return;
    const shown = mapped.attachment.name;
    expect(shown).toContain('\\u{7}');
    expect(shown).toContain('\\n');
    expect(shown).toContain('\\u{202e}');
    expect(/[\p{Cc}\p{Cf}]/u.test(shown)).toBe(false);

    const skipped = mapAttachment(
      {
        name,
        path: `/root/${name.replace('.png', '.bin')}`,
        body: undefined,
        contentType: 'application/octet-stream',
      },
      { sensitive: false },
    );
    expect(skipped.ok).toBe(false);
    if (skipped.ok) return;
    expect(skipped.code).toBe('attachment_unmapped_type');
    expect(/[\p{Cc}\p{Cf}]/u.test(skipped.message)).toBe(false);
    expect(skipped.message).not.toContain('/root');
  });

  it('skips an undefined, empty, wildcard or comma-list content type as a mismatch', () => {
    const values: unknown[] = [
      undefined,
      '',
      'application/octet-stream',
      'image/*',
      'image/png, image/jpeg',
    ];
    for (const contentType of values) {
      const outcome = mapAttachment(
        {
          name: 'shot.png',
          path: '/root/shot.png',
          body: undefined,
          contentType: contentType as string,
        },
        { sensitive: false },
      );
      expect(outcome.ok).toBe(false);
      if (!outcome.ok) expect(outcome.code).toBe('attachment_type_mismatch');
    }
  });
});
