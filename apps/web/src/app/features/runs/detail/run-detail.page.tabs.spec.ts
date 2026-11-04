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
