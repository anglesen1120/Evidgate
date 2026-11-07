// The exception list is the only way a violation may be accepted, so its matching is the thing that
// must be exact: an entry covers one rule id on one element, and a scan of any other rule or any
// other element still fails. These cases pin that, plus the report lines a failing scan prints.
import {
  AXE_JSDOM_RUN_OPTIONS,
  AXE_RUN_OPTIONS,
  formatViolations,
  isExcepted,
  targetSelector,
  unexceptedViolations,
  type A11yException,
} from '../../../../e2e/a11y/axe-config';

const EXCEPTION: A11yException = {
  rule: 'color-contrast',
  selector: 'p.notice',
  reason: 'Placeholder copy sits on the raised surface until the token change lands.',
  owner: 'web-ui',
  'review-by': '2027-01-31',
};

function violationOf(rule: string, ...targets: unknown[]) {
  return {
    id: rule,
    impact: 'serious' as const,
    nodes: targets.map((target) => ({
      target,
      failureSummary: 'Fix any of the following:\n  Element has insufficient color contrast',
    })),
  };
}

describe('the shared axe options', () => {
  it('runs the whole WCAG 2.2 AA tag set in a real browser', () => {
    expect(AXE_RUN_OPTIONS.runOnly).toEqual({
      type: 'tag',
      values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'],
    });
    expect(AXE_RUN_OPTIONS.rules).toBeUndefined();
  });

  it('keeps the same tag set for jsdom and disables only contrast there', () => {
    expect(AXE_JSDOM_RUN_OPTIONS.runOnly).toEqual(AXE_RUN_OPTIONS.runOnly);
    expect(AXE_JSDOM_RUN_OPTIONS.rules).toEqual({ 'color-contrast': { enabled: false } });
  });
});

describe('exception matching', () => {
  it('covers the exact reported target and the last step of a longer path', () => {
    expect(isExcepted([EXCEPTION], 'color-contrast', 'p.notice')).toBe(true);
    expect(isExcepted([EXCEPTION], 'color-contrast', '#main-content > p.notice')).toBe(true);
    expect(isExcepted([EXCEPTION], 'color-contrast', '#main-content p.notice')).toBe(true);
    expect(isExcepted([EXCEPTION], 'color-contrast', ['iframe', 'p.notice'])).toBe(true);
  });

  it('never covers another rule id', () => {
    expect(isExcepted([EXCEPTION], 'target-size', 'p.notice')).toBe(false);
    expect(isExcepted([EXCEPTION], 'link-name', 'p.notice')).toBe(false);
  });

  it('never covers another element, a prefix, or an empty target', () => {
    expect(isExcepted([EXCEPTION], 'color-contrast', 'p.notice-strong')).toBe(false);
    expect(isExcepted([EXCEPTION], 'color-contrast', 'p.notice > span')).toBe(false);
    expect(isExcepted([EXCEPTION], 'color-contrast', 'p.notice-x')).toBe(false);
    expect(isExcepted([EXCEPTION], 'color-contrast', '')).toBe(false);
    expect(isExcepted([EXCEPTION], 'color-contrast', [])).toBe(false);
  });

  it('scopes a qualified entry to its own parent path', () => {
    const scoped: A11yException = { ...EXCEPTION, selector: '#main-content > p.notice' };
    expect(isExcepted([scoped], 'color-contrast', '#main-content > p.notice')).toBe(true);
    expect(isExcepted([scoped], 'color-contrast', '#unrelated > p.notice')).toBe(false);
    expect(isExcepted([scoped], 'color-contrast', '#other > section > p.notice')).toBe(false);
    expect(isExcepted([scoped], 'color-contrast', 'p.notice')).toBe(false);
  });

  it('waives nothing when the list is empty', () => {
    expect(isExcepted([], 'color-contrast', 'p.notice')).toBe(false);
  });

  it('reads the deepest selector of a frame chain', () => {
    expect(targetSelector(['iframe#preview', 'p.notice'])).toBe('p.notice');
    expect(targetSelector('  #a   >\n p.notice ')).toBe('#a > p.notice');
  });
});

describe('the violations a scan still fails on', () => {
  it('drops only the excepted node of a rule, keeping the others', () => {
    const remaining = unexceptedViolations(
      [violationOf('color-contrast', 'p.notice', 'p.other')],
      [EXCEPTION],
    );
    expect(remaining).toHaveLength(1);
    expect(remaining[0]?.nodes.map((node) => node.target)).toEqual(['p.other']);
  });

  it('drops the whole violation once every node is excepted', () => {
    const reported = [violationOf('color-contrast', 'p.notice')];
    expect(unexceptedViolations(reported, [EXCEPTION])).toEqual([]);
  });

  it('keeps every other rule of the same scan', () => {
    const remaining = unexceptedViolations(
      [violationOf('color-contrast', 'p.notice'), violationOf('link-name', 'a.icon')],
      [EXCEPTION],
    );
    expect(remaining.map((violation) => violation.id)).toEqual(['link-name']);
  });

  it('reports one line per node, with the rule, the impact and the element', () => {
    const lines = formatViolations(
      unexceptedViolations(
        [violationOf('color-contrast', '#a > p.other'), violationOf('link-name', 'a.icon')],
        [EXCEPTION],
      ),
    );
    expect(lines).toHaveLength(2);
    expect(lines[0]).toBe(
      'color-contrast (serious) at #a > p.other: Fix any of the following: ' +
        'Element has insufficient color contrast',
    );
    expect(lines[1]).toContain('link-name (serious) at a.icon');
  });

  it('reports nothing for a clean scan', () => {
    expect(formatViolations(unexceptedViolations([], [EXCEPTION]))).toEqual([]);
  });
});
