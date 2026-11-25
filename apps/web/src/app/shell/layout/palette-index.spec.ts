import type { NavDestinationView } from '../nav/nav.models';
import {
  buildPaletteSections,
  foldForSearch,
  parseRunId,
  type PaletteInput,
  type PaletteLabels,
  type PaletteSection,
} from './palette-index';

const labels: PaletteLabels = {
  navigate: 'Navigate',
  appearance: 'Appearance',
  language: 'Language',
  runs: 'Runs',
  openRun: 'Open run',
  theme: {
    light: 'Switch to light theme',
    dark: 'Switch to dark theme',
    system: 'Switch to system theme',
  },
  locale: { en: 'Switch to English', vi: 'Switch to Vietnamese' },
};

const RUN_ID = '0198a000-0000-7000-8000-00000000abcd';

const destinations: NavDestinationView[] = [
  { id: 'overview', label: 'Tổng quan', link: ['/orgs', 'org', 'projects', 'project', 'overview'] },
  {
    id: 'feature-tree',
    label: 'Feature tree',
    link: ['/orgs', 'org', 'projects', 'project', 'features'],
  },
  {
    id: 'billing',
    label: 'Billing & Audit',
    link: ['/orgs', 'org', 'billing'],
    groupLabel: 'Access & Security',
  },
];

function input(overrides: Partial<PaletteInput> = {}): PaletteInput {
  return {
    destinations,
    orgId: 'org',
    projectId: 'project',
    localeHrefs: { vi: '/vi/orgs/org' },
    labels,
    ...overrides,
  };
}

function ids(sections: readonly PaletteSection[]): string[] {
  return sections.flatMap((section) => section.items.map((item) => item.id));
}

describe('foldForSearch', () => {
  it('ignores case and Vietnamese diacritics', () => {
    expect(foldForSearch('Tổng quan')).toBe('tong quan');
  });

  it('maps đ and Đ to d, which has no Unicode decomposition', () => {
    expect(foldForSearch('Đường dẫn')).toBe('duong dan');
    expect(foldForSearch('ĐƯỜNG')).toBe('duong');
  });
});

describe('parseRunId', () => {
  it('accepts a UUID and lowercases it', () => {
    expect(parseRunId(`  ${RUN_ID.toUpperCase()} `)).toBe(RUN_ID);
  });

  it('rejects text that is not a UUID', () => {
    expect(parseRunId('run-1')).toBeNull();
    expect(parseRunId('0198a000-0000-7000-8000-00000000abc')).toBeNull();
  });
});

describe('buildPaletteSections', () => {
  it('lists every destination, theme and locale for an empty query', () => {
    expect(ids(buildPaletteSections(input(), ''))).toEqual([
      'nav:overview',
      'nav:feature-tree',
      'nav:billing',
      'theme:light',
      'theme:dark',
      'theme:system',
      'locale:vi',
    ]);
  });

  it('treats a whitespace-only query as empty', () => {
    expect(ids(buildPaletteSections(input(), '   '))).toEqual(
      ids(buildPaletteSections(input(), '')),
    );
  });

  it('matches labels case- and diacritic-insensitively', () => {
    expect(ids(buildPaletteSections(input(), 'TONG QUAN'))).toEqual(['nav:overview']);
  });

  it('keeps a group label from an entry that carries groupLabel', () => {
    const sections = buildPaletteSections(input(), 'billing');
    expect(sections.map((section) => section.label)).toEqual(['Access & Security']);
    expect(ids(sections)).toEqual(['nav:billing']);
  });

  it('returns no results for a query that matches nothing', () => {
    expect(buildPaletteSections(input(), 'no-such-screen')).toEqual([]);
  });

  it('omits the locale command when no bundle path exists', () => {
    expect(ids(buildPaletteSections(input({ localeHrefs: {} }), ''))).not.toContain('locale:vi');
  });

  it('offers the run action for a UUID when org and project are present', () => {
    const sections = buildPaletteSections(input(), RUN_ID.toUpperCase());
    expect(ids(sections)).toEqual([`run:${RUN_ID}`]);
    expect(sections[0]!.items[0]!.action).toEqual({
      kind: 'navigate',
      link: ['/orgs', 'org', 'projects', 'project', 'runs', RUN_ID],
    });
  });

  it('offers no run action without an org or project', () => {
    expect(buildPaletteSections(input({ orgId: null }), RUN_ID)).toEqual([]);
    expect(buildPaletteSections(input({ projectId: null }), RUN_ID)).toEqual([]);
  });

  it('keeps groups in first-appearance order', () => {
    expect(buildPaletteSections(input(), '').map((section) => section.label)).toEqual([
      'Navigate',
      'Access & Security',
      'Appearance',
      'Language',
    ]);
  });
});
