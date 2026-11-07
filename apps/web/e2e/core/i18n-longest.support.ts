// Longest-string layout probes for the core flows: the `vi` catalogue is the source of truth for
// what a page can render, so the probes are derived from it at run time instead of being a second
// hand-copied list that drifts. A unit whose template never reaches a page is simply not probed
// there; each page asserts its own coverage floor, so an empty match cannot pass silently.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import type { Page } from '@playwright/test';

import { RUN_DETAIL_GRANT } from '../shell/run-detail.support';
import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  withVariants,
  type ShellLocale,
} from '../shell/support';

const VI_CATALOGUE = resolve(__dirname, '../../src/locale/messages.vi.xlf');

/** Templates the six core flows render. */
const CORE_DIRECTORIES = [
  'src/app/features/auth/',
  'src/app/features/project/',
  'src/app/features/runs/',
  'src/app/features/onboarding/',
  'src/app/shell/',
  'src/app/shared/ui/',
];

/** The same areas by id prefix, for units whose `<context>` block the catalogue omits. */
const CORE_AREAS = ['auth.', 'shell.', 'runs.', 'onboarding.', 'shared.'];

/** Pages outside the core flows: the shared-UI gallery and the not-yet-built route stub. */
const OUTSIDE_CORE_FLOWS = ['shared.gallery.', 'shell.stub.'];

/** Shorter static fragments match too much (a label, a common word) to prove the element. */
const MIN_PROBE_LENGTH = 8;

/** One translated unit of the `vi` catalogue, reduced to the text a page can actually render. */
export interface ViString {
  readonly id: string;
  /** The whole target with each inline placeholder collapsed to a single space. */
  readonly text: string;
  /** The static pieces between placeholders: what survives in the DOM after interpolation. */
  readonly segments: readonly string[];
  readonly length: number;
}

/** One tracked string that the page under test currently renders, with the fragments to find it. */
export interface PageString {
  readonly id: string;
  readonly text: string;
  readonly probes: readonly string[];
}

export interface PageGeometry {
  readonly viewport: number;
  readonly document: number;
  readonly body: number;
}

export type ClippingKind = 'clipped' | 'outside-viewport';

/** A longest string the page renders while cutting it off or pushing it past the viewport. */
export interface ClippingFinding {
  readonly stringId: string;
  readonly text: string;
  readonly selector: string;
  readonly kind: ClippingKind;
  readonly scrollWidth: number;
  readonly clientWidth: number;
  readonly left: number;
  readonly right: number;
  readonly viewport: number;
}

interface Probe {
  readonly id: string;
  readonly text: string;
  readonly probe: string;
}

let catalogue: readonly ViString[] | null = null;

/** Every core-flow target of the `vi` catalogue, longest first; parsed once per worker. */
export function loadViStrings(): readonly ViString[] {
  if (catalogue === null) {
    catalogue = readCatalogue(readFileSync(VI_CATALOGUE, 'utf8'));
  }
  return catalogue;
}

/**
 * The longest tracked strings the page renders, at most `limit`. Order follows the catalogue, so
 * the first entries are the longest strings of the whole core flow that this page shows.
 */
export function longestOnPage(
  strings: readonly ViString[],
  pageText: string,
  limit = 20,
): readonly PageString[] {
  const rendered = normalizeText(pageText);
  const found: PageString[] = [];
  for (const string of strings) {
    if (found.length === limit) {
      break;
    }
    const probes = string.segments.filter(
      (segment) => segment.length >= MIN_PROBE_LENGTH && rendered.includes(segment),
    );
    if (probes.length > 0) {
      found.push({ id: string.id, text: string.text, probes });
    }
  }
  return found;
}

/** The rendered page text the probe match runs against. */
export async function pageText(page: Page): Promise<string> {
  return page.evaluate(() => document.body.innerText);
}

/** The page's reflow geometry: the document and the body against the viewport. */
export async function pageGeometry(page: Page): Promise<PageGeometry> {
  return page.evaluate(() => ({
    viewport: document.documentElement.clientWidth,
    document: document.documentElement.scrollWidth,
    body: document.body.scrollWidth,
  }));
}

/** The strings the page paints at all, and the ones it cannot paint in full. */
export interface ScanResult {
  readonly matched: readonly string[];
  readonly findings: readonly ClippingFinding[];
}

/** Every element holding one of these strings that clips it or leaves the viewport horizontally. */
export async function scanLongestStrings(
  page: Page,
  strings: readonly PageString[],
): Promise<ScanResult> {
  const probes: Probe[] = strings.flatMap((string) =>
    string.probes.map((probe) => ({
      id: string.id,
      text: string.text.slice(0, 80),
      probe,
    })),
  );
  const measured = await page.evaluate(inPageMeasure, probes);
  const seen = new Set<string>();
  const matched = new Set<string>();
  const findings: ClippingFinding[] = [];
  for (const measurement of measured.measurements) {
    if (!measurement.rendered || measurement.width < 2 || measurement.height < 2) {
      continue;
    }
    // Clipped-away text proves nothing about what the page paints, so it is dropped entirely.
    if (insideHiddenClip(measurement.ancestors)) {
      continue;
    }
    matched.add(measurement.probeId);
    const finding = findingFor(measurement, measured.viewport);
    if (finding === null) {
      continue;
    }
    const key = `${finding.stringId}|${finding.selector}|${finding.kind}`;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    findings.push(finding);
  }
  return { matched: [...matched], findings };
}

/** One element's own box and the clipping-relevant style of every ancestor up to the root. */
interface AncestorBox {
  readonly selector: string;
  readonly overflowX: string;
  readonly textOverflow: string;
  readonly scrollWidth: number;
  readonly clientWidth: number;
}

interface PageMeasurement {
  readonly probeId: string;
  readonly text: string;
  readonly selector: string;
  readonly rendered: boolean;
  readonly width: number;
  readonly height: number;
  readonly left: number;
  readonly right: number;
  readonly ancestors: readonly AncestorBox[];
}

interface MeasuredPage {
  readonly viewport: number;
  readonly measurements: readonly PageMeasurement[];
}

/** `sr-only` shaves a wrapper to 1px and clips it, so nothing inside reaches a sighted reader. */
function insideHiddenClip(ancestors: readonly AncestorBox[]): boolean {
  return ancestors.some((row) => row.clientWidth <= 1 && row.overflowX !== 'visible');
}

/**
 * Runs inside the page, so Playwright serialises the source and every helper it uses is declared
 * here. It only measures; the judgement of what counts as clipped runs in the test process.
 */
function inPageMeasure(probes: Probe[]): MeasuredPage {
  const viewport = document.documentElement.clientWidth;
  const measurements: PageMeasurement[] = [];
  const collapse = (value: string | null) => (value ?? '').replace(/\s+/g, ' ').trim();
  const describe = (element: Element): string => {
    const testId = element.getAttribute('data-testid');
    const hook = testId === null ? '' : `[data-testid="${testId}"]`;
    const first = collapse(element.getAttribute('class')).split(' ').filter(Boolean)[0];
    const cls = first === undefined ? '' : `.${first}`;
    const id = element.id === '' ? '' : `#${element.id}`;
    return `${element.tagName.toLowerCase()}${id}${hook}${cls}`;
  };
  const ancestry = (element: Element): AncestorBox[] => {
    const rows: AncestorBox[] = [];
    for (let node: Element | null = element; node !== null; node = node.parentElement) {
      const style = getComputedStyle(node);
      rows.push({
        selector: describe(node),
        overflowX: style.overflowX,
        textOverflow: style.textOverflow,
        scrollWidth: node.scrollWidth,
        clientWidth: node.clientWidth,
      });
    }
    return rows;
  };

  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const element = node.parentElement;
    const text = collapse(node.nodeValue);
    if (element === null || text === '') {
      continue;
    }
    // An inert subtree (a closed off-canvas drawer) and non-content tags are never painted.
    if (element.closest('script, style, noscript, template, [inert]')) {
      continue;
    }
    const style = getComputedStyle(element);
    const box = element.getBoundingClientRect();
    for (const probe of probes) {
      if (!text.includes(probe.probe)) {
        continue;
      }
      measurements.push({
        probeId: probe.id,
        text: probe.text,
        selector: describe(element),
        rendered: style.display !== 'none' && style.visibility !== 'hidden',
        width: box.width,
        height: box.height,
        left: box.left,
        right: box.right,
        ancestors: ancestry(element),
      });
    }
  }
  return { viewport, measurements };
}

/** A measurement that a reader cannot read in full, or `null` when the string is fully legible. */
function findingFor(measurement: PageMeasurement, viewport: number): ClippingFinding | null {
  if (!measurement.rendered || measurement.width < 2 || measurement.height < 2) {
    return null; // sr-only or not painted: nothing a reader could see cut off
  }
  // Content inside a scroll region is reachable, so widening it past the box is not a cut.
  const scroller = measurement.ancestors.some(
    (row) =>
      (row.overflowX === 'auto' || row.overflowX === 'scroll') &&
      row.scrollWidth > row.clientWidth + 1,
  );
  const clipped = measurement.ancestors.find(
    (row) =>
      row.scrollWidth > row.clientWidth + 1 &&
      (row.overflowX === 'hidden' ||
        row.overflowX === 'clip' ||
        (row.textOverflow === 'ellipsis' && row.overflowX !== 'visible')),
  );
  const outside = measurement.left < -1 || measurement.right > viewport + 1;
  if (clipped === undefined && !(outside && !scroller)) {
    return null;
  }
  const own = measurement.ancestors[0];
  const box = clipped ?? own;
  return {
    stringId: measurement.probeId,
    text: measurement.text,
    selector: clipped === undefined ? measurement.selector : clipped.selector,
    kind: clipped === undefined ? 'outside-viewport' : 'clipped',
    scrollWidth: box === undefined ? 0 : box.scrollWidth,
    clientWidth: box === undefined ? 0 : box.clientWidth,
    left: Math.round(measurement.left),
    right: Math.round(measurement.right),
    viewport,
  };
}

/** The review address of the run list the core flow opens its detail from. */
export function coreRunListPath(locale: ShellLocale): string {
  return withVariants(
    localePath(locale, `/dev/run-list/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`),
    [RUN_DETAIL_GRANT, '?fixture.variant.listRuns=runs'],
  );
}

function readCatalogue(xml: string): readonly ViString[] {
  const strings: ViString[] = [];
  let cursor = 0;
  for (;;) {
    const unit = findElement(xml, 'trans-unit', cursor);
    if (unit === null) {
      break;
    }
    cursor = unit.end;
    const id = attribute(unit.attributes, 'id');
    if (id === null || !isCoreFlowUnit(id, sourceFiles(unit.body))) {
      continue;
    }
    const target = findElement(unit.body, 'target');
    const segments = target === null ? [] : segmentsOf(target.body);
    const text = segments.join(' ');
    if (text !== '') {
      strings.push({ id, text, segments, length: text.length });
    }
  }
  return strings.sort((left, right) => right.length - left.length);
}

function isCoreFlowUnit(id: string, files: readonly string[]): boolean {
  if (OUTSIDE_CORE_FLOWS.some((area) => id.startsWith(area))) {
    return false;
  }
  if (files.length > 0) {
    return files.some((file) => CORE_DIRECTORIES.some((directory) => file.startsWith(directory)));
  }
  return CORE_AREAS.some((area) => id.startsWith(area));
}

function sourceFiles(body: string): readonly string[] {
  const files: string[] = [];
  let cursor = 0;
  for (;;) {
    const context = findElement(body, 'context', cursor);
    if (context === null) {
      break;
    }
    cursor = context.end;
    if (attribute(context.attributes, 'context-type') === 'sourcefile') {
      files.push(normalizeText(context.body));
    }
  }
  return files;
}

/** The static text of a target: inline `<x/>` placeholders and their wrappers are dropped. */
function segmentsOf(target: string): readonly string[] {
  const segments: string[] = [];
  let cursor = 0;
  for (;;) {
    const tag = target.indexOf('<', cursor);
    const end = tag < 0 ? target.length : tag;
    const segment = normalizeText(target.slice(cursor, end));
    if (segment !== '') {
      segments.push(segment);
    }
    if (tag < 0) {
      return segments;
    }
    const close = target.indexOf('>', tag);
    if (close < 0) {
      return segments;
    }
    cursor = close + 1;
  }
}

interface XmlElement {
  readonly attributes: string;
  readonly body: string;
  readonly end: number;
}

/** The first `<tag>…</tag>` at or after `from`; the catalogue's tags never nest by that name. */
function findElement(xml: string, tag: string, from = 0): XmlElement | null {
  let cursor = from;
  for (;;) {
    const open = xml.indexOf(`<${tag}`, cursor);
    if (open < 0) {
      return null;
    }
    const boundary = xml.charAt(open + tag.length + 1);
    const delimited = boundary === '>' || boundary === '/' || boundary.trim() === '';
    if (!delimited) {
      cursor = open + 1;
      continue;
    }
    const tagEnd = endOfTag(xml, open);
    if (tagEnd < 0) {
      return null;
    }
    const attributes = xml.slice(open + tag.length + 1, tagEnd);
    if (attributes.endsWith('/')) {
      return { attributes, body: '', end: tagEnd + 1 };
    }
    const close = xml.indexOf(`</${tag}>`, tagEnd + 1);
    if (close < 0) {
      return null;
    }
    return { attributes, body: xml.slice(tagEnd + 1, close), end: close + tag.length + 3 };
  }
}

function endOfTag(xml: string, open: number): number {
  let quote = '';
  for (let index = open; index < xml.length; index += 1) {
    const char = xml.charAt(index);
    if (quote !== '') {
      if (char === quote) {
        quote = '';
      }
      continue;
    }
    if (char === '"' || char === "'") {
      quote = char;
      continue;
    }
    if (char === '>') {
      return index;
    }
  }
  return -1;
}

function attribute(attributes: string, name: string): string | null {
  const key = `${name}="`;
  const start = attributes.indexOf(key);
  if (start < 0) {
    return null;
  }
  const valueStart = start + key.length;
  const valueEnd = attributes.indexOf('"', valueStart);
  return valueEnd < 0 ? null : attributes.slice(valueStart, valueEnd);
}

function normalizeText(value: string): string {
  return decodeEntities(value).replace(/\s+/g, ' ').trim();
}

/** The five named entities XML allows, plus the numeric forms `numericEntity` decodes. */
const NAMED_ENTITIES: Readonly<Record<string, string>> = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
};

function decodeEntities(value: string): string {
  if (!value.includes('&')) {
    return value;
  }
  let decoded = '';
  let cursor = 0;
  for (;;) {
    const amp = value.indexOf('&', cursor);
    if (amp < 0) {
      return decoded + value.slice(cursor);
    }
    decoded += value.slice(cursor, amp);
    const semi = value.indexOf(';', amp);
    if (semi < 0) {
      return decoded + value.slice(amp);
    }
    const entity = value.slice(amp + 1, semi);
    const replacement = NAMED_ENTITIES[entity] ?? numericEntity(entity);
    decoded += replacement === null ? `&${entity};` : replacement;
    cursor = semi + 1;
  }
}

function numericEntity(entity: string): string | null {
  const hexadecimal = entity.startsWith('#x') || entity.startsWith('#X');
  if (!hexadecimal && !entity.startsWith('#')) {
    return null;
  }
  const code = Number.parseInt(entity.slice(hexadecimal ? 2 : 1), hexadecimal ? 16 : 10);
  return Number.isNaN(code) ? null : String.fromCodePoint(code);
}
