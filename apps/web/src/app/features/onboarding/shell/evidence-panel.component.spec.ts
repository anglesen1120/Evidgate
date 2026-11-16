import { registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
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
    const panel = root.querySelector('section[aria-labelledby="onboarding-evidence-title"]');
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
    expect(root.querySelector('section')?.getAttribute('aria-labelledby')).toBe(
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
          activated_at: '2026-11-04T12:00:00.123456+00:00',
        },
      },
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    expect(root.textContent).toContain('policy-revision-42');
    const instant = '2026-11-04T12:00:00.123456+00:00';
    expect(root.textContent).toContain('Activated at (UTC)');
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe(instant);
    expect(root.querySelector('time')?.textContent).toBe('11/4/2026, 12:00:00\u202fPM');
    expect(root.textContent).not.toContain(instant);
    expect(root.querySelector('[data-resource-state="empty"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\bPASS\b|\bFAIL\b|enabled and collecting/i);
  });

  it('does not format a malformed activation instant or invent a time for it', async () => {
    fixture.componentRef.setInput('progress', {
      ...EMPTY,
      project: PROJECT,
      activation: {
        kind: 'active',
        state: {
          project_id: PROJECT.id,
          policy_revision_id: 'revision-1',
          hash: 'sha256:confirmed',
          mode: 'advisory',
          activated_by: 'member-1',
          activated_at: 'not-an-instant',
        },
      },
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    expect(root.querySelector('time')).toBeNull();
    expect(root.textContent).not.toContain('not-an-instant');
    expect(root.textContent).toContain('Created resources are unavailable.');
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

describe('EvidencePanelComponent localized activation time', () => {
  it('uses Vietnamese UTC formatting and preserves the exact offset ISO value', async () => {
    registerLocaleData(localeVi);
    await TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: 'vi' }],
    }).compileComponents();
    const instant = '2026-11-04T19:00:00.123456+07:00';
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    fixture.componentRef.setInput('progress', {
      ...EMPTY,
      project: PROJECT,
      activation: {
        kind: 'active',
        state: {
          project_id: PROJECT.id,
          policy_revision_id: 'revision-1',
          hash: 'sha256:confirmed',
          mode: 'advisory',
          activated_by: 'member-1',
          activated_at: instant,
        },
      },
    } satisfies OnboardingProgress);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const time = root.querySelector('time');
    expect(time?.getAttribute('datetime')).toBe(instant);
    expect(time?.textContent).toBe('12:00:00 04/11/2026');
    expect(time?.textContent).not.toBe('19:00:00 04/11/2026');
  });
});
