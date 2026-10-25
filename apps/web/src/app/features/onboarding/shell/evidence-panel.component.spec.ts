import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { ProjectDto } from '@evidgate/sdk';

import type { StepNumber } from '../data/step-derivation';
import { EvidencePanelComponent } from './evidence-panel.component';

const PROJECT: ProjectDto = {
  id: 'project-1',
  org_id: 'org-1',
  name: 'Storefront web',
  slug: 'storefront-web',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-10-25T00:00:00Z',
  updated_at: '2026-10-25T00:00:00Z',
  version: 1,
  retention_days: null,
  monthly_run_quota: 5000,
  case_prefix: 'STOR',
};

describe('EvidencePanelComponent', () => {
  let fixture: ComponentFixture<EvidencePanelComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(EvidencePanelComponent);
    fixture.componentRef.setInput('step', 1);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it.each([
    [1, 'The created project'],
    [2, 'Connection resources'],
    [3, 'Sample-run evidence'],
    [4, 'Comparison resources'],
    [5, 'Activation resources'],
  ] satisfies [StepNumber, string][])(
    'keeps a named, step-specific empty slot for step %i',
    async (step, text) => {
      fixture.componentRef.setInput('step', step);
      await fixture.whenStable();
      const panel = root.querySelector('[data-testid="onboarding-evidence-panel"]')!;
      expect(panel.tagName).toBe('ASIDE');
      expect(panel.getAttribute('aria-labelledby')).toBe('onboarding-evidence-title');
      expect(panel.textContent).toContain(text);
      expect(panel.textContent).toContain('Nothing produced yet for this step.');
      expect(panel.textContent).not.toMatch(/\bPASS\b|\bFAIL\b|enabled and collecting/i);
    },
  );

  it('shows the real project summary for later steps, never a fabricated project', async () => {
    fixture.componentRef.setInput('step', 2);
    expect(root.querySelector('dl')).toBeNull();
    fixture.componentRef.setInput('project', PROJECT);
    await fixture.whenStable();
    expect(root.querySelector('dl')?.textContent).toContain(PROJECT.name);
    expect(root.querySelector('dl')?.textContent).toContain(PROJECT.id);
    fixture.componentRef.setInput('step', 1);
    await fixture.whenStable();
    expect(root.querySelector('dl')).toBeNull();
  });

  it('renders server-supplied project text as text, not HTML', async () => {
    fixture.componentRef.setInput('step', 3);
    fixture.componentRef.setInput('project', { ...PROJECT, name: '<img src=x onerror=alert(1)>' });
    await fixture.whenStable();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(root.querySelector('img, script')).toBeNull();
  });
});
