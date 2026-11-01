import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MISSING_CODES, type DecisionViewModel } from '../decision/decision-view-model';
import { EvidencePanelComponent } from './evidence-panel.component';
import { missingReasonEntries, type ConfirmedMissingTarget } from './missing-reasons';

type TargetKind = 'producer' | 'attempt' | 'none';

/**
 * The closed eight-code vocabulary of 00 §8.5, with the explanation the screen must show and the
 * only identity kind each literal may name.
 */
const REASONS = [
  ['missing_shard', 'Shard evidence is missing.', 'producer'],
  ['count_mismatch', 'Collected and expected counts do not match.', 'producer'],
  ['artifact_missing', 'A required artifact is missing.', 'none'],
  ['build_mismatch', 'The reported build does not match the manifest.', 'none'],
  ['unknown_status', 'A reported result status is not recognized.', 'attempt'],
  ['setup_error', 'Test setup reported an error.', 'attempt'],
  ['stale_epoch', 'Evidence belongs to a stale producer epoch.', 'producer'],
  ['missing_result', 'An expected test result is missing.', 'attempt'],
] as const satisfies readonly (readonly [string, string, TargetKind])[];

const producerTarget: ConfirmedMissingTarget = {
  code: 'missing_shard',
  target: { kind: 'producer', shard: 'shard-3' },
};
const attemptTarget: ConfirmedMissingTarget = {
  code: 'unknown_status',
  target: { kind: 'attempt', executionKey: 'exec_42', attemptNo: 2 },
};

type EvaluatedDecision = Extract<DecisionViewModel, { kind: 'evaluated' }>;

function evaluated(overrides: Partial<Omit<EvaluatedDecision, 'kind'>> = {}): EvaluatedDecision {
  return {
    kind: 'evaluated',
    decision: 'FAIL',
    evidenceRevision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: [],
    reasons: [],
    policyHash: 'policy-hash-1',
    manifestHash: 'manifest-hash-1',
    evaluatorVersion: 'evaluator-1.4.0',
    mode: 'live',
    ...overrides,
  };
}

describe('missing reason vocabulary', () => {
  it('covers exactly the closed eight-code set, so a ninth code cannot pass unnoticed', () => {
    expect(REASONS.map(([code]) => code)).toEqual([...MISSING_CODES]);
  });

  it.each(REASONS)('renders %s verbatim with its explanation', (code, explanation, kind) => {
    const entries = missingReasonEntries([code]);
    expect(entries).toHaveLength(1);
    expect(entries[0]).toEqual({
      code,
      text: kind === 'none' ? explanation : `${explanation} Target details unavailable`,
    });
  });

  it.each(REASONS.filter(([, , kind]) => kind === 'producer'))(
    'links %s only to a confirmed producer slot',
    (code, explanation) => {
      const entries = missingReasonEntries([code], [{ code, target: producerTarget.target }]);
      expect(entries[0]).toEqual({ code, text: explanation });
      expect(entries[1]).toEqual({
        code: 'shard-3',
        text: 'View producer shard-3',
        href: '#producer-shard-3',
      });
    },
  );

  it.each(REASONS.filter(([, , kind]) => kind === 'attempt'))(
    'links %s only to a confirmed attempt',
    (code, explanation) => {
      const entries = missingReasonEntries([code], [{ code, target: attemptTarget.target }]);
      expect(entries[0]).toEqual({ code, text: explanation });
      expect(entries[1]).toEqual({
        code: 'exec_42',
        text: 'View attempt 2 for exec_42',
        href: '#attempt-exec_42-2',
      });
    },
  );

  it.each(REASONS.filter(([, , kind]) => kind !== 'none'))(
    'keeps %s unlinked with an explicit target-unavailable note when nothing is confirmed',
    (code, explanation) => {
      const entries = missingReasonEntries([code]);
      expect(entries).toHaveLength(1);
      expect(entries[0]).toEqual({
        code,
        text: `${explanation} Target details unavailable`,
      });
    },
  );

  it.each(REASONS.filter(([, , kind]) => kind === 'none'))(
    'never invents a target for %s, even when a relation is supplied',
    (code, explanation) => {
      const entries = missingReasonEntries([code], [{ code, target: producerTarget.target }]);
      expect(entries).toHaveLength(1);
      expect(entries[0]).toEqual({ code, text: explanation });
    },
  );

  it('ignores a relation whose identity kind the literal cannot name', () => {
    const entries = missingReasonEntries(
      ['missing_shard'],
      [{ code: 'missing_shard', target: attemptTarget.target }],
    );
    expect(entries).toHaveLength(1);
    expect(entries[0]?.href).toBeUndefined();
    expect(entries[0]?.text).toContain('Target details unavailable');
  });

  it('preserves server order and duplicates and lists every confirmed target', () => {
    const entries = missingReasonEntries(
      ['missing_result', 'missing_shard', 'missing_shard'],
      [producerTarget, { code: 'missing_shard', target: { kind: 'producer', shard: 'shard-4' } }],
    );
    expect(entries.map((entry) => entry.code)).toEqual([
      'missing_result',
      'missing_shard',
      'shard-3',
      'shard-4',
      'missing_shard',
      'shard-3',
      'shard-4',
    ]);
    expect(entries.filter((entry) => entry.href !== undefined)).toHaveLength(4);
  });

  it('keeps an unknown literal as escaped diagnostic text, never as a ninth reason', () => {
    const entries = missingReasonEntries(['shard_gone\u202e<script>']);
    expect(entries).toHaveLength(1);
    expect(entries[0]?.code).toBe('shard_gone⟨U+202E⟩<script>');
    expect(entries[0]?.text).toBe('Evidence response unavailable: shard_gone⟨U+202E⟩<script>');
    expect(entries[0]?.href).toBeUndefined();
  });
});

describe('MissingReasonList rendering', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(
    decision: DecisionViewModel,
    confirmedTargets: readonly ConfirmedMissingTarget[] = [],
  ) {
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    fixture.componentRef.setInput('decision', decision);
    fixture.componentRef.setInput('confirmedTargets', confirmedTargets);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders all eight literals in server order with the confirmed links between them', async () => {
    const root = await render(evaluated({ missing: REASONS.map(([code]) => code) }), [
      producerTarget,
      attemptTarget,
    ]);
    const codes = [...root.querySelectorAll('.reason-code')].map((code) =>
      code.textContent?.trim(),
    );
    expect(codes).toEqual([
      'missing_shard',
      'shard-3',
      'count_mismatch',
      'artifact_missing',
      'build_mismatch',
      'unknown_status',
      'exec_42',
      'setup_error',
      'stale_epoch',
      'missing_result',
    ]);
    const links = [...root.querySelectorAll<HTMLAnchorElement>('.reason-link')];
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '#producer-shard-3',
      '#attempt-exec_42-2',
    ]);
    expect(links.map((link) => link.textContent?.trim())).toEqual([
      'View producer shard-3',
      'View attempt 2 for exec_42',
    ]);
  });

  it('explains every literal even when no target can be named', async () => {
    const root = await render(evaluated({ missing: REASONS.map(([code]) => code) }));
    expect(root.querySelectorAll('.reason-link')).toHaveLength(0);
    const texts = [...root.querySelectorAll('.reason-text')].map((text) =>
      text.textContent?.trim(),
    );
    expect(texts).toEqual(
      REASONS.map(([, explanation, kind]) =>
        kind === 'none' ? explanation : `${explanation} Target details unavailable`,
      ),
    );
    expect(root.querySelector('.reason-code')).not.toBeNull();
  });

  it('renders an unknown literal as text, creating no element from it', async () => {
    const root = await render(evaluated({ missing: ['shard_gone\u202e<script>'] }));
    expect(root.querySelector('script')).toBeNull();
    expect(root.querySelector('.reason-code')?.textContent).toBe('shard_gone⟨U+202E⟩<script>');
    expect(root.querySelector('.reason-text')?.textContent).toContain(
      'Evidence response unavailable:',
    );
  });
});
