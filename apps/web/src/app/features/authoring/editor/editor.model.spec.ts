import {
  bodyOf,
  codePoints,
  EDITOR_READ,
  EDITOR_WRITE,
  textError,
  type CaseDraft,
} from './editor.model';

describe('manual case editor model', () => {
  it('uses canonical authoring permission codes', () => {
    expect(EDITOR_READ).toBe('authoring:read');
    expect(EDITOR_WRITE).toBe('authoring:write');
  });
  it('counts Unicode code points rather than UTF-16 units', () => {
    expect(codePoints('')).toBe(0);
    expect(codePoints('A😀ắ')).toBe(3);
    expect(codePoints('\ud800')).toBe(1);
    expect(textError('😀'.repeat(500), 500, true)).toBeNull();
    expect(textError('😀'.repeat(501), 500, true)).not.toBeNull();
  });
  it('enforces required text and each inclusive server length boundary', () => {
    expect(textError(' \n ', 500, true)).not.toBeNull();
    expect(textError('', 4000, false)).toBeNull();
    for (const limit of [500, 2000, 4000]) {
      expect(textError('x'.repeat(limit), limit)).toBeNull();
      expect(textError('x'.repeat(limit + 1), limit)).not.toBeNull();
    }
  });
  it('serializes paired steps without UI ids, blank optional data or unsupported reason', () => {
    const draft: CaseDraft = {
      title: 'Title',
      description: '',
      precondition: '',
      priority: 'high',
      steps: [
        { id: 'local-id', no: 1, action: 'Act', expected: 'Expect', data: '' },
        { id: 'other-id', no: 2, action: 'Act again', expected: 'Expect again', data: 'Inputs' },
      ],
    };
    expect(bodyOf(draft)).toEqual({
      title: 'Title',
      description: null,
      precondition: null,
      priority: 'high',
      steps: [
        { no: 1, action: 'Act', expected: 'Expect' },
        { no: 2, action: 'Act again', expected: 'Expect again', data: 'Inputs' },
      ],
    });
    expect(bodyOf(draft)).not.toHaveProperty('reason');
    expect(draft.steps[0]?.id).toBe('local-id');
  });
});
