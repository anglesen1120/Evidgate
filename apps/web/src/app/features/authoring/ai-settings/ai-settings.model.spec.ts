import type { AiSettingsDocument } from '@evidgate/sdk';
import head from '../../../../../../../fixtures/web-api/ai-settings-head/input.json';
import { count, normalized, submission } from './ai-settings.model';

describe('AI settings Unicode submission boundaries', () => {
  it('counts trimmed NFC code points rather than UTF-16 units', () => {
    expect(count('  e\u0301😀  ')).toBe(2);
    expect(normalized('  e\u0301😀  ')).toBe('é😀');
  });
  it('normalizes the request without trimming or truncating the editable draft', () => {
    // The controlled fixture is validated against OpenAPI by the sibling contract spec.
    const draft = {
      ...head.body.settings,
      environment_url: '   ',
      writing_style: '  e\u0301😀  ',
    } as AiSettingsDocument;
    const result = submission(draft);
    expect(result.writing_style).toBe('é😀');
    expect(result.environment_url).toBeNull();
    expect(draft.writing_style).toBe('  e\u0301😀  ');
    expect(draft.environment_url).toBe('   ');
  });
});
