import { localeBundlePath } from '../../../shell/layout/locale-switcher.component';
import { decisionReasonExplanation } from './reason-explanation';
import { runValueLabel, type RunValueKind } from './run-value-label';

describe('run presentation unknown values', () => {
  it.each<RunValueKind>(['purpose', 'attempt', 'identity', 'producer', 'decision', 'mode'])(
    'preserves an unfamiliar %s value instead of inventing a known state',
    (kind) => {
      expect(runValueLabel(kind, 'future_wire_value')).toBe('future_wire_value');
      expect(runValueLabel(kind, 'constructor')).toBe('constructor');
      expect(runValueLabel(kind, '__proto__')).toBe('__proto__');
    },
  );
  it.each(['', 'future_reason', 'constructor', '__proto__', 'missing_shard_extra'])(
    'does not invent an explanation for unknown diagnostic %s',
    (reason) => expect(decisionReasonExplanation(reason)).toBeNull(),
  );
  it('retains distinct explanations for an actual failure and missing evidence', () => {
    expect(decisionReasonExplanation('required_assertion_failed')).toBe(
      'A required assertion failed.',
    );
    expect(decisionReasonExplanation('missing_shard')).toBe('Shard evidence is missing.');
    expect(runValueLabel('attempt', 'failed')).toBe('Failed');
    expect(runValueLabel('attempt', 'passed')).toBe('Passed');
  });
});

describe('locale bundle URL opaque suffixes', () => {
  it.each([
    '/orgs/a%2Fb/projects/p/runs?ref=refs%2Fheads%2Fmain&tag=%E1%BB%87#part%20two',
    '/orgs/org/projects/project/overview?q=a%26b%3Dc&empty=#%C4%91%E1%BB%81',
    '/orgs/org/projects/project/runs?return=%2Fvi%2Fa%3Fx%3D1#%2F%3F%23',
    '//not-an-origin.example/path?x=1#section',
  ])('retains encoded route, query and fragment without decoding: %s', (suffix) => {
    expect(localeBundlePath('https://app.example/review/vi/', 'vi-VN', suffix)).toBe(
      `/review/en${suffix}`,
    );
    expect(localeBundlePath('https://app.example/review/en/', 'en-US', suffix)).toBe(
      `/review/vi${suffix}`,
    );
  });
  it('does not interpret a locale-looking encoded deployment segment as the bundle', () => {
    expect(localeBundlePath('https://app.example/%76i/', 'vi', '/runs')).toBeNull();
  });
});
