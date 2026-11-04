import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { RunDataService } from '../data/run-data.service';
import { routes } from '../runs.routes';
import { RunDetailPage } from './run-detail.page';
import { liveBody, revision, revisionPage, run } from './run-detail-store.fixtures';

describe('RunDetailPage evidence and decision tabs', () => {
  provideLayoutTestStubs();
  const get = vi.fn();
  const listAttempts = vi.fn();
  const listEvidenceRevisions = vi.fn();
  const getDecision = vi.fn();
  beforeEach(() => {
    get.mockReset().mockResolvedValue({ ...run, state: 'incomplete' });
    listAttempts.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    listEvidenceRevisions
      .mockReset()
      .mockResolvedValue(
        revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
      );
    getDecision.mockReset().mockResolvedValue(liveBody());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function open() {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(
      `/projects/project-1/runs/${run.run_id}?evidence_revision=3`,
      RunDetailPage,
    );
    await harness.fixture.whenStable();
    const root = harness.routeNativeElement;
    if (!root) throw new Error('Run detail was not rendered');
    const tabs = [...root.querySelectorAll<HTMLButtonElement>('[role="tab"]')];
    const evidence = tabs.find((tab) => tab.textContent?.trim() === 'Evidence');
    const decision = tabs.find((tab) => tab.textContent?.trim() === 'Latest live decision');
    if (!evidence || !decision) throw new Error('Detail tab triggers were not rendered');
    return { harness, root, evidence, decision };
  }

  it('labels both mounted panels and starts with only evidence visible without rereading on selection', async () => {
    const { root, harness, evidence, decision } = await open();
    const tablist = root.querySelector('[role="tablist"]');
    expect(tablist?.getAttribute('aria-label')).toBe('Run sections');
    expect(tablist?.getAttribute('aria-label')).not.toBe(root.querySelector('h1')?.textContent);
    const panels = [...root.querySelectorAll<HTMLElement>('[role="tabpanel"]')];
    expect(panels).toHaveLength(2);
    expect(evidence.getAttribute('aria-selected')).toBe('true');
    expect(decision.tabIndex).toBe(-1);
    for (const tab of [evidence, decision]) {
      const panel = panels.find((entry) => entry.id === tab.getAttribute('aria-controls'));
      expect(panel?.getAttribute('aria-labelledby')).toBe(tab.id);
    }
    expect(
      root.querySelector('app-evidence-panel')?.closest<HTMLElement>('[role="tabpanel"]')?.hidden,
    ).toBe(false);
    expect(
      root.querySelector('app-decision-view')?.closest<HTMLElement>('[role="tabpanel"]')?.hidden,
    ).toBe(true);
    decision.click();
    await harness.fixture.whenStable();
    expect(decision.getAttribute('aria-selected')).toBe('true');
    expect(
      root.querySelector('app-decision-view')?.closest<HTMLElement>('[role="tabpanel"]')?.hidden,
    ).toBe(false);
    expect(get).toHaveBeenCalledTimes(1);
    expect(listAttempts).toHaveBeenCalledTimes(1);
    expect(listEvidenceRevisions).toHaveBeenCalledTimes(1);
    expect(getDecision).toHaveBeenCalledTimes(1);
  });

  it('announces an evidence change once, outside the hidden panel, while the decision tab is active', async () => {
    const { root, harness, evidence, decision } = await open();
    decision.click();
    await harness.fixture.whenStable();
    const regions = [...root.querySelectorAll<HTMLElement>('[data-evidence-announcement]')];
    expect(regions).toHaveLength(1);
    const region = regions[0];
    if (!region) throw new Error('Evidence announcement region was not rendered');
    expect(region.getAttribute('role')).toBe('status');
    expect(region.getAttribute('aria-live')).toBe('polite');
    // Outside both panels: a live region inside the hidden evidence panel is never spoken.
    expect(region.closest('[role="tabpanel"]')).toBeNull();
    expect(region.closest('[hidden]')).toBeNull();
    expect(region.textContent?.trim()).toBe('');

    await harness.navigateByUrl(
      `/projects/project-1/runs/${run.run_id}?evidence_revision=4`,
      RunDetailPage,
    );
    await harness.fixture.whenStable();
    expect(
      root.querySelector('app-evidence-panel')?.closest<HTMLElement>('[role="tabpanel"]')?.hidden,
    ).toBe(true);
    expect(region.textContent).toContain('Viewing evidence revision 4');
    const spoken = [...root.querySelectorAll<HTMLElement>('[role="status"]')].filter((entry) =>
      entry.textContent?.includes('Viewing evidence revision'),
    );
    expect(spoken).toEqual([region]);

    // Showing the panel neither adds a second region nor repeats the message in the panel.
    evidence.click();
    await harness.fixture.whenStable();
    expect(root.querySelectorAll('[data-evidence-announcement]')).toHaveLength(1);
    expect(root.querySelector('app-evidence-panel')?.textContent).not.toContain(
      'Viewing evidence revision',
    );
    expect(region.textContent).toContain('Viewing evidence revision 4');
  });

  it('keeps both panels out of the tab order and only the selected panel visible', async () => {
    const { root, harness, decision } = await open();
    const panels = [...root.querySelectorAll<HTMLElement>('[role="tabpanel"]')];
    expect(panels).toHaveLength(2);
    const evidencePanel = panels.find(
      (panel) => panel.querySelector('app-evidence-panel') !== null,
    );
    const decisionPanel = panels.find((panel) => panel.querySelector('app-decision-view') !== null);
    if (!evidencePanel || !decisionPanel) throw new Error('Detail panels were not rendered');
    // Brain hardcodes tabindex="0" on every panel; each panel here holds its own focusable
    // controls, so the page removes the panel stop and those controls stay the tab stops.
    for (const panel of panels) {
      expect(panel.hasAttribute('tabindex')).toBe(false);
      expect(panel.tabIndex).toBe(-1);
    }
    const stops = [
      ...root.querySelectorAll<HTMLElement>('a[href], button, select, textarea, [tabindex]'),
    ];
    expect(stops).not.toContain(evidencePanel);
    expect(stops).not.toContain(decisionPanel);
    expect(evidencePanel.hidden).toBe(false);
    expect(decisionPanel.hidden).toBe(true);
    expect(evidencePanel.querySelector('[role="combobox"]')).not.toBeNull();

    decision.click();
    await harness.fixture.whenStable();
    expect(evidencePanel.hidden).toBe(true);
    expect(decisionPanel.hidden).toBe(false);
    expect(root.querySelectorAll('[role="tabpanel"][hidden]')).toHaveLength(1);
    expect(root.querySelector<HTMLElement>('[role="tabpanel"][hidden]')).toBe(evidencePanel);
  });

  it('supports arrow, Home and End keyboard selection with roving focus', async () => {
    const { harness, evidence, decision } = await open();
    evidence.focus();
    evidence.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowRight', keyCode: 39, bubbles: true }),
    );
    await harness.fixture.whenStable();
    expect(document.activeElement).toBe(decision);
    expect(decision.getAttribute('aria-selected')).toBe('true');
    decision.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Home', keyCode: 36, bubbles: true }),
    );
    await harness.fixture.whenStable();
    expect(document.activeElement).toBe(evidence);
    evidence.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'End', keyCode: 35, bubbles: true }),
    );
    await harness.fixture.whenStable();
    expect(document.activeElement).toBe(decision);
  });

  it('reveals evidence before focusing it from the summary or decision mismatch action', async () => {
    const { root, harness, evidence, decision } = await open();
    decision.click();
    await harness.fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[data-run-missing-summary] button')?.click();
    await harness.fixture.whenStable();
    expect(evidence.getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement?.id).toBe('evidence-title');
    decision.click();
    await harness.fixture.whenStable();
    const action = root.querySelector<HTMLButtonElement>('[data-decision-mismatch] button');
    expect(action).not.toBeNull();
    action?.focus();
    action?.click();
    await harness.fixture.whenStable();
    expect(evidence.getAttribute('aria-selected')).toBe('true');
    expect(root.querySelector('[data-decision-mismatch]')).toBeNull();
    expect(document.activeElement?.id).toBe('evidence-title');
  });
});
