import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { ProjectDto } from '@evidgate/sdk';

import type { OnboardingProgress } from '../data/onboarding-progress.service';
import { NO_STEPS_DONE } from '../data/step-derivation';
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
const EMPTY: OnboardingProgress = {
  project: null,
  done: NO_STEPS_DONE,
  activation: { kind: 'none' },
  sampleRun: null,
  bindings: [],
  tokens: [],
  bindingsTruncated: false,
  tokensTruncated: false,
  currentStep: 1,
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
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('keeps the resource card named and read-only without adding keyboard stops', () => {
    const panel = root.querySelector('aside');
    expect(panel?.getAttribute('data-slot')).toBe('card');
    expect(panel?.getAttribute('aria-labelledby')).toBe(root.querySelector('h2')?.id);
    expect(root.querySelector('button, a, [tabindex]')).toBeNull();
  });

  it('reports unavailable rather than empty before resources are known', () => {
    expect(root.querySelector('[data-resource-state="unavailable"]')?.textContent).toContain(
      'Created resources are unavailable',
    );
    expect(root.querySelector('[data-resource-state="empty"]')).toBeNull();
  });

  it('shows empty only for a confirmed absence of setup resources', async () => {
    fixture.componentRef.setInput('progress', EMPTY);
    await fixture.whenStable();
    expect(root.querySelector('[data-resource-state="empty"]')?.textContent).toContain(
      'No setup resources have been created yet',
    );
    expect(root.querySelector('dl')).toBeNull();
    expect(root.querySelector('[data-resource-state="unavailable"]')).toBeNull();
  });

  it('shows a confirmed project even when the current step is still one', async () => {
    fixture.componentRef.setInput('progress', { ...EMPTY, project: PROJECT });
    await fixture.whenStable();
    expect(root.querySelector('dl')?.textContent).toContain(PROJECT.name);
    expect(root.querySelector('dl')?.textContent).toContain(PROJECT.id);
    expect(root.querySelector('[data-resource-state="empty"]')).toBeNull();
    expect(root.textContent).not.toContain('will appear here');
    expect(root.querySelector('aside')?.getAttribute('aria-labelledby')).toBe(
      'onboarding-evidence-title',
    );
  });

  it('lists the confirmed activation without inventing a release verdict', async () => {
    fixture.componentRef.setInput('progress', {
      ...EMPTY,
      project: PROJECT,
      activation: {
        kind: 'active',
        state: {
          project_id: PROJECT.id,
          policy_revision_id: 'policy-revision-42',
          hash: 'sha256:confirmed-policy',
          mode: 'advisory',
          activated_by: 'member-1',
          activated_at: '2026-11-04T12:00:00Z',
        },
      },
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    expect(root.textContent).toContain('policy-revision-42');
    expect(root.textContent).toContain('2026-11-04T12:00:00Z');
    expect(root.querySelector('[data-resource-state="empty"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b|enabled and collecting/i);
  });

  it('keeps known project details when activation access is unavailable', async () => {
    fixture.componentRef.setInput('progress', {
      ...EMPTY,
      project: PROJECT,
      activation: { kind: 'forbidden' },
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    expect(root.textContent).toContain(PROJECT.name);
    expect(root.querySelector('[data-resource-state="unavailable"]')?.textContent).toContain(
      'current access',
    );
    expect(root.querySelector('[data-resource-state="empty"]')).toBeNull();
  });

  it('labels retained resources as last confirmed after a refresh failure', async () => {
    fixture.componentRef.setInput('progress', { ...EMPTY, project: PROJECT });
    fixture.componentRef.setInput('refreshFailed', true);
    await fixture.whenStable();
    expect(root.textContent).toContain(PROJECT.name);
    expect(root.querySelector('[role="status"]')?.textContent).toContain(
      'last confirmed resources',
    );
  });

  it('renders server-supplied project text as text, not HTML', async () => {
    fixture.componentRef.setInput('progress', {
      ...EMPTY,
      project: { ...PROJECT, name: '<img src=x onerror=alert(1)>' },
    });
    await fixture.whenStable();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(root.querySelector('img, script')).toBeNull();
  });
});
