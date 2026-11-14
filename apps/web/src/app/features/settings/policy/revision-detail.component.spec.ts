import { formatDate } from '@angular/common';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { PolicyRevisionDto } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { RevisionDetailComponent } from './revision-detail.component';
import { PolicyStore } from './policy.store';

provideLayoutTestStubs();
const policyBody = {
  flaky_budget: 0,
  required_suites: ['suite-identifier'],
  critical_cases: ['critical-id'],
  quarantine_ids: ['quarantine-id'],
  future_field: false,
};
const revision: PolicyRevisionDto = {
  id: 'revision-one',
  project_id: 'project',
  revision_no: 1,
  hash: 'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
  state: 'active',
  created_by: 'actor-id',
  created_at: '2026-11-01T10:00:00+07:00',
  version: 1,
  body: policyBody,
};

describe('RevisionDetailComponent', () => {
  const projectId = signal<string | null>('project');
  const loadBody = vi.fn<(id: string) => Promise<PolicyRevisionDto>>();

  beforeEach(() => {
    projectId.set('project');
    loadBody.mockReset().mockResolvedValue(revision);
    TestBed.configureTestingModule({
      imports: [RevisionDetailComponent],
      providers: [{ provide: PolicyStore, useValue: { projectId, loadBody } }],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render(id: string | null = revision.id) {
    const fixture = TestBed.createComponent(RevisionDetailComponent);
    fixture.componentRef.setInput('revisionId', id);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('shows full copyable metadata, UTC time, zero and unknown fields without hiding the body', async () => {
    const { fixture, root } = await render();
    const values = fixture.debugElement
      .queryAll(By.directive(CopyableIdentifierComponent))
      .map((element) => element.componentInstance as CopyableIdentifierComponent);
    const hash = values.find((value) => value.value() === revision.hash);
    expect(hash?.truncate()).toBe(false);
    expect(root.querySelector('[data-policy-selected-metadata]')?.textContent).toContain(
      revision.hash,
    );
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe(revision.created_at);
    expect(root.querySelector('time')?.textContent?.trim()).toBe(
      `${formatDate(revision.created_at, 'medium', 'en-US', 'UTC')} UTC`,
    );
    expect(root.querySelector('[data-policy-field="flaky_budget"]')?.textContent).toContain('0');
    expect(root.querySelector('[data-policy-field="future_field"]')?.textContent).toContain(
      'false',
    );
    expect(root.querySelector('[data-policy-field="required_suites"]')?.textContent).toContain(
      'suite-identifier',
    );
  });

  it('groups all supplied critical and quarantine identifiers without inventing lifecycle data', async () => {
    const { fixture, root } = await render();
    const tables = fixture.debugElement
      .queryAll(By.directive(DataTableComponent))
      .map(
        (element) =>
          element.componentInstance as DataTableComponent<{ index: number; value: string }>,
      );
    expect(tables).toHaveLength(2);
    expect(tables[0]?.rows()).toEqual([{ index: 0, value: 'critical-id' }]);
    expect(tables[1]?.rows()).toEqual([{ index: 0, value: 'quarantine-id' }]);
    expect(root.textContent).toContain(
      'Names, quarantine lifecycle, reasons and expiry are not supplied',
    );
    expect(root.textContent).not.toContain('Expires in');
  });

  it('distinguishes empty references and escapes supplied reference identifiers', async () => {
    loadBody.mockResolvedValue({
      ...revision,
      body: { critical_cases: [], quarantine_ids: ['<img src=x>'] },
    });
    const { root } = await render();
    expect(
      root.querySelector('[data-policy-reference-group="critical_cases"]')?.textContent,
    ).toContain('No references');
    expect(
      root.querySelector('[data-policy-reference-group="quarantine_ids"]')?.textContent,
    ).toContain('<img src=x>');
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('[data-policy-values]')?.textContent?.trim()).toBe('');
  });

  it('keeps all 201 returned references available to the shared bounded table', async () => {
    const references = Array.from({ length: 201 }, (_unused, index) => `critical-${index}`);
    loadBody.mockResolvedValue({ ...revision, body: { critical_cases: references } });
    const { fixture, root } = await render();
    const table = fixture.debugElement.query(By.directive(DataTableComponent))
      .componentInstance as DataTableComponent<{ index: number; value: string }>;
    expect(table.rows()).toHaveLength(201);
    expect(table.rows().at(-1)?.value).toBe('critical-200');
    expect(
      root.querySelector('[data-policy-reference-group="quarantine_ids"]')?.textContent,
    ).toContain('Not supplied');
  });

  it('localizes a draft state while preserving its recorded enum in the detail', async () => {
    loadBody.mockResolvedValue({ ...revision, state: 'draft' });
    const { root } = await render();
    expect(root.querySelector('app-policy-revision-state app-status-pill')?.textContent).toContain(
      'Draft',
    );
    expect(root.querySelector('[data-policy-selected-metadata]')?.textContent).toContain('draft');
  });

  it('provides deliberate retry on failure and never presents the previous selected body as current', async () => {
    loadBody.mockRejectedValueOnce(new Error('private diagnostic'));
    const { fixture, root } = await render();
    expect(root.textContent).toContain('The selected revision could not be loaded');
    expect(root.textContent).not.toContain('private diagnostic');
    expect(root.querySelector('[data-policy-values]')).toBeNull();
    root.querySelector<HTMLButtonElement>('app-inline-feedback button')?.click();
    await fixture.whenStable();
    expect(loadBody).toHaveBeenCalledTimes(2);
    expect(root.querySelector('[data-policy-values]')).not.toBeNull();
  });

  it('discards an older selection response after a later selection has settled', async () => {
    const pending = new Subject<PolicyRevisionDto>();
    loadBody.mockReturnValueOnce(firstValueFrom(pending));
    const { fixture, root } = await render();
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    const second = { ...revision, id: 'revision-two', revision_no: 2 };
    loadBody.mockResolvedValue(second);
    fixture.componentRef.setInput('revisionId', second.id);
    await fixture.whenStable();
    pending.next(revision);
    pending.complete();
    await fixture.whenStable();
    expect(root.querySelector('[data-policy-selected-metadata]')?.textContent).toContain(second.id);
    expect(root.querySelector('[data-policy-selected-metadata]')?.textContent).not.toContain(
      revision.id,
    );
  });

  it('clears details on project removal and performs no read without a selected revision', async () => {
    const { fixture, root } = await render(null);
    expect(loadBody).not.toHaveBeenCalled();
    expect(root.textContent).toContain('Select a revision');
    fixture.componentRef.setInput('revisionId', revision.id);
    await fixture.whenStable();
    projectId.set(null);
    await fixture.whenStable();
    expect(root.querySelector('[data-policy-values]')).toBeNull();
    expect(root.textContent).not.toContain(revision.hash);
  });
});
