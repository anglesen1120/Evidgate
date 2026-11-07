// Geometry of one longest-string match: measured inside the page, judged in the test process.
// Vertical overflow is judged as well as horizontal, because `height` or `max-height` cuts the
// later lines of a translated paragraph with no horizontal trace at all.
export type ClippingKind = 'clipped' | 'clipped-vertically' | 'outside-viewport';

/** A longest string the page renders while cutting it off or pushing it past the viewport. */
export interface ClippingFinding {
  readonly stringId: string;
  readonly text: string;
  readonly selector: string;
  readonly kind: ClippingKind;
  readonly scrollWidth: number;
  readonly clientWidth: number;
  readonly scrollHeight: number;
  readonly clientHeight: number;
  readonly left: number;
  readonly right: number;
  readonly top: number;
  readonly bottom: number;
  readonly viewport: number;
}

/** One fragment of a tracked string, as handed to the in-page measurement. */
export interface Probe {
  readonly id: string;
  readonly text: string;
  readonly probe: string;
}

/** One element's own box and the clipping-relevant style of every ancestor up to the root. */
export interface AncestorBox {
  readonly selector: string;
  readonly overflowX: string;
  readonly overflowY: string;
  readonly textOverflow: string;
  readonly scrollWidth: number;
  readonly clientWidth: number;
  readonly scrollHeight: number;
  readonly clientHeight: number;
  /** The padding box vertically: the part of the box that can show content at all. */
  readonly clientTop: number;
  readonly clientBottom: number;
}

export interface PageMeasurement {
  readonly probeId: string;
  readonly text: string;
  readonly selector: string;
  readonly rendered: boolean;
  readonly width: number;
  readonly height: number;
  readonly left: number;
  readonly right: number;
  readonly top: number;
  readonly bottom: number;
  readonly ancestors: readonly AncestorBox[];
}

export interface MeasuredPage {
  readonly viewport: number;
  readonly measurements: readonly PageMeasurement[];
}

/** `sr-only` shaves a wrapper to 1px on one axis and clips it, so nothing inside is visible. */
export function insideHiddenClip(ancestors: readonly AncestorBox[]): boolean {
  return ancestors.some(
    (row) =>
      (row.clientWidth <= 1 && row.overflowX !== 'visible') ||
      (row.clientHeight <= 1 && row.overflowY !== 'visible'),
  );
}

/**
 * Runs inside the page, so Playwright serialises the source and every helper it uses is declared
 * here. It only measures; the judgement of what counts as clipped runs in the test process.
 */
export function inPageMeasure(probes: Probe[]): MeasuredPage {
  const viewport = document.documentElement.clientWidth;
  const measurements: PageMeasurement[] = [];
  const collapse = (value: string | null) => (value ?? '').replace(/\s+/g, ' ').trim();
  const describe = (element: Element): string => {
    const hook = element.getAttribute('data-testid');
    const cls = collapse(element.getAttribute('class')).split(' ')[0];
    const id = element.id === '' ? '' : `#${element.id}`;
    const testId = hook === null ? '' : `[data-testid="${hook}"]`;
    const suffix = cls === undefined ? '' : `.${cls}`;
    return `${element.tagName.toLowerCase()}${id}${testId}${suffix}`;
  };
  const box = (element: Element): AncestorBox => {
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    const offset = element instanceof HTMLElement ? element.clientTop : 0;
    return {
      selector: describe(element),
      overflowX: style.overflowX,
      overflowY: style.overflowY,
      textOverflow: style.textOverflow,
      scrollWidth: element.scrollWidth,
      clientWidth: element.clientWidth,
      scrollHeight: element.scrollHeight,
      clientHeight: element.clientHeight,
      clientTop: rect.top + offset,
      clientBottom: rect.top + offset + element.clientHeight,
    };
  };
  const ancestry = (element: Element): AncestorBox[] => {
    const rows: AncestorBox[] = [];
    for (let node: Element | null = element; node !== null; node = node.parentElement) {
      rows.push(box(node));
    }
    return rows;
  };
  // An open modal covers the rest of the page, so text behind it is occluded rather than cut.
  const root = document.querySelector('[aria-modal="true"]') ?? document.body;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
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
    const rect = element.getBoundingClientRect();
    const ancestors = ancestry(element);
    for (const probe of probes) {
      if (!text.includes(probe.probe)) {
        continue;
      }
      measurements.push({
        probeId: probe.id,
        text: probe.text,
        selector: describe(element),
        rendered: style.display !== 'none' && style.visibility !== 'hidden',
        width: rect.width,
        height: rect.height,
        left: rect.left,
        right: rect.right,
        top: rect.top,
        bottom: rect.bottom,
        ancestors,
      });
    }
  }
  return { viewport, measurements };
}

/**
 * The box that cuts the rest of the text off vertically, if any. `auto` and `scroll` keep the
 * hidden lines reachable, so they are not cuts, the same reason a scrollable table is not one.
 */
function verticalClip(measurement: PageMeasurement): AncestorBox | undefined {
  const own = measurement.ancestors[0];
  return measurement.ancestors.find((row) => {
    if (row.overflowY !== 'hidden' && row.overflowY !== 'clip') {
      return false;
    }
    if (row.scrollHeight <= row.clientHeight + 1) {
      return false;
    }
    if (row === own) {
      return true;
    }
    return measurement.top < row.clientTop - 1 || measurement.bottom > row.clientBottom + 1;
  });
}

/** A measurement that a reader cannot read in full, or `null` when the string is fully legible. */
export function findingFor(measurement: PageMeasurement, viewport: number): ClippingFinding | null {
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
  const vertical = verticalClip(measurement);
  const outside = measurement.left < -1 || measurement.right > viewport + 1;
  if (vertical === undefined && clipped === undefined && !(outside && !scroller)) {
    return null;
  }
  const box = vertical ?? clipped ?? measurement.ancestors[0];
  return {
    stringId: measurement.probeId,
    text: measurement.text,
    selector: box === undefined ? measurement.selector : box.selector,
    kind:
      vertical !== undefined
        ? 'clipped-vertically'
        : clipped === undefined
          ? 'outside-viewport'
          : 'clipped',
    scrollWidth: box === undefined ? 0 : box.scrollWidth,
    clientWidth: box === undefined ? 0 : box.clientWidth,
    scrollHeight: box === undefined ? 0 : box.scrollHeight,
    clientHeight: box === undefined ? 0 : box.clientHeight,
    left: Math.round(measurement.left),
    right: Math.round(measurement.right),
    top: Math.round(measurement.top),
    bottom: Math.round(measurement.bottom),
    viewport,
  };
}
