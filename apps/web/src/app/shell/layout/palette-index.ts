import type { NavDestinationView } from '../nav/nav.models';

export type PaletteThemeChoice = 'light' | 'dark' | 'system';

export type PaletteAction =
  | { readonly kind: 'navigate'; readonly link: readonly string[] }
  | { readonly kind: 'theme'; readonly choice: PaletteThemeChoice }
  | { readonly kind: 'locale'; readonly href: string };

export interface PaletteItem {
  readonly id: string;
  readonly label: string;
  readonly group: string;
  readonly action: PaletteAction;
}

export interface PaletteSection {
  readonly id: string;
  readonly label: string;
  readonly items: readonly PaletteItem[];
}

export interface PaletteLabels {
  readonly navigate: string;
  readonly appearance: string;
  readonly language: string;
  readonly runs: string;
  readonly openRun: string;
  readonly theme: Readonly<Record<PaletteThemeChoice, string>>;
  readonly locale: Readonly<Record<'en' | 'vi', string>>;
}

export interface PaletteInput {
  /** Already filtered by phase, permission and scope; registry order. */
  readonly destinations: readonly NavDestinationView[];
  readonly orgId: string | null;
  readonly projectId: string | null;
  /** Only the locale the current page can switch to; absent when no bundle path exists. */
  readonly localeHrefs: Readonly<Partial<Record<'en' | 'vi', string>>>;
  readonly labels: PaletteLabels;
}

const THEME_CHOICES: readonly PaletteThemeChoice[] = ['light', 'dark', 'system'];
const LOCALES = ['en', 'vi'] as const;
const RUN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Case- and diacritic-insensitive key; Vietnamese đ has no decomposition, so map it explicitly. */
export function foldForSearch(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').replace(/[đĐ]/g, 'd').toLowerCase();
}

export function parseRunId(query: string): string | null {
  const term = query.trim();
  return RUN_ID.test(term) ? term.toLowerCase() : null;
}

export function buildPaletteSections(input: PaletteInput, query: string): PaletteSection[] {
  const term = foldForSearch(query.trim());
  const matched = [...staticItems(input), ...runItems(input, query)].filter(
    (item) => term === '' || foldForSearch(`${item.label} ${item.group}`).includes(term),
  );
  return groupInOrder(matched);
}

function staticItems(input: PaletteInput): PaletteItem[] {
  const { labels } = input;
  const navigation = input.destinations.map((destination): PaletteItem => ({
    id: `nav:${destination.id}`,
    label: destination.label,
    group: destination.groupLabel ?? labels.navigate,
    action: { kind: 'navigate', link: destination.link },
  }));
  const themes = THEME_CHOICES.map((choice): PaletteItem => ({
    id: `theme:${choice}`,
    label: labels.theme[choice],
    group: labels.appearance,
    action: { kind: 'theme', choice },
  }));
  const locales = LOCALES.flatMap((code): PaletteItem[] => {
    const href = input.localeHrefs[code];
    return href
      ? [
          {
            id: `locale:${code}`,
            label: labels.locale[code],
            group: labels.language,
            action: { kind: 'locale', href },
          },
        ]
      : [];
  });
  return [...navigation, ...themes, ...locales];
}

function runItems(input: PaletteInput, query: string): PaletteItem[] {
  const runId = parseRunId(query);
  if (runId === null || input.orgId === null || input.projectId === null) return [];
  return [
    {
      id: `run:${runId}`,
      label: `${input.labels.openRun} ${runId}`,
      group: input.labels.runs,
      action: {
        kind: 'navigate',
        link: ['/orgs', input.orgId, 'projects', input.projectId, 'runs', runId],
      },
    },
  ];
}

// Sections keep first-appearance order so the flattened list matches what the user sees.
function groupInOrder(items: readonly PaletteItem[]): PaletteSection[] {
  const sections = new Map<string, PaletteItem[]>();
  for (const item of items) {
    const list = sections.get(item.group);
    if (list) list.push(item);
    else sections.set(item.group, [item]);
  }
  return [...sections].map(([label, list]) => ({ id: label, label, items: list }));
}
