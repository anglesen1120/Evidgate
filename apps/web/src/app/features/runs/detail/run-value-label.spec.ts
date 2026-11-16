import { clearTranslations, loadTranslations } from '@angular/localize';
import { runValueLabel, type RunValueKind } from './run-value-label';

const VALUES = [
  ['purpose', 'regular', 'Regular run'],
  ['purpose', 'replay', 'Replay'],
  ['attempt', 'passed', 'Passed'],
  ['attempt', 'failed', 'Failed'],
  ['attempt', 'skipped', 'Skipped'],
  ['attempt', 'timed_out', 'Timed out'],
  ['attempt', 'error', 'Error'],
  ['attempt', 'blocked', 'Blocked'],
  ['identity', 'bound', 'Bound'],
  ['identity', 'unbound', 'Unbound'],
  ['producer', 'open', 'Open'],
  ['producer', 'finalized', 'Finalized'],
  ['decision', 'evaluated', 'Evaluated'],
  ['decision', 'pending', 'Not evaluated'],
  ['decision', 'unavailable', 'Unavailable'],
  ['mode', 'live', 'Live evaluation'],
] as const satisfies readonly (readonly [RunValueKind, string, string])[];

describe('runValueLabel', () => {
  afterEach(() => clearTranslations());

  it.each(VALUES)('labels known %s value %s', (kind, code, label) => {
    expect(runValueLabel(kind, code)).toBe(label);
  });

  it.each(['future_status', '__proto__', 'constructor', '', 'regular'])(
    'preserves unknown attempt value %s instead of borrowing another enum label',
    (code) => expect(runValueLabel('attempt', code)).toBe(code),
  );

  it('preserves unknown diagnostics while escaping format controls', () => {
    expect(runValueLabel('identity', 'future\u202e<script>')).toBe('future⟨U+202E⟩<script>');
  });

  it('uses translated labels for application enums without translating the raw fallback', () => {
    loadTranslations({
      'runs.value.purpose-regular': 'Lượt chạy thông thường',
      'runs.value.status-passed': 'Đạt',
      'runs.value.identity-bound': 'Đã liên kết',
      'runs.value.producer-finalized': 'Đã chốt',
      'runs.value.decision-evaluated': 'Đã đánh giá',
      'runs.value.decision-live': 'Đánh giá trực tiếp',
    });
    expect(runValueLabel('purpose', 'regular')).toBe('Lượt chạy thông thường');
    expect(runValueLabel('attempt', 'passed')).toBe('Đạt');
    expect(runValueLabel('identity', 'bound')).toBe('Đã liên kết');
    expect(runValueLabel('producer', 'finalized')).toBe('Đã chốt');
    expect(runValueLabel('decision', 'evaluated')).toBe('Đã đánh giá');
    expect(runValueLabel('mode', 'live')).toBe('Đánh giá trực tiếp');
    expect(runValueLabel('purpose', 'customer_purpose')).toBe('customer_purpose');
  });
});
