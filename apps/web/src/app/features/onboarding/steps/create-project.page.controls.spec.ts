import { provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { OnboardingStore } from '../onboarding.store';
import { OnboardingViewport } from '../onboarding-viewport.testing';
import { CreateProjectPage } from './create-project.page';
import { requiredElement } from './step-controls.testing';

describe('CreateProjectPage Helm controls', () => {
  let fixture: ComponentFixture<CreateProjectPage>;
  let root: HTMLElement;
  let viewport: OnboardingViewport;
  const permitted = signal(true);
  const createProject = vi.fn<OnboardingDataService['createProject']>();

  beforeEach(async () => {
    permitted.set(true);
    viewport = new OnboardingViewport();
    createProject.mockReset();
    await TestBed.configureTestingModule({
      imports: [CreateProjectPage],
      providers: [
        provideZonelessChangeDetection(),
        viewport.provider,
        { provide: OnboardingDataService, useValue: { createProject } },
        { provide: OnboardingStore, useValue: {} },
        { provide: SessionStore, useValue: { has: () => permitted() } },
        { provide: Router, useValue: { url: '/orgs/org-1/onboarding' } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(CreateProjectPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('keeps native required fields labelled and keyboard focusable', () => {
    for (const id of ['onboarding-project-name', 'onboarding-project-slug']) {
      const field = requiredElement<HTMLInputElement>(root, `#${id}`);
      expect(field.getAttribute('data-slot')).toBe('input');
      expect(field.required).toBe(true);
      expect(field.labels?.item(0)?.htmlFor).toBe(id);
      expect(field.labels?.item(0)?.textContent).not.toContain('*');
      field.focus();
      expect(document.activeElement).toBe(field);
      expect(field.tabIndex).toBe(0);
    }
    const submit = requiredElement<HTMLButtonElement>(root, 'button[type="submit"]');
    expect(submit.getAttribute('data-slot')).toBe('button');
    expect(submit.disabled).toBe(false);
  });

  it('states requiredness once without unexplained label markers', () => {
    expect(root.textContent?.match(/All fields required unless marked optional\./g)).toHaveLength(
      1,
    );
    expect(root.querySelector('label [aria-hidden="true"]')).toBeNull();
  });

  it('maps visible validation into the Helm invalid state and its description', async () => {
    const field = requiredElement<HTMLInputElement>(root, '#onboarding-project-name');
    field.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(field.getAttribute('data-matches-spartan-invalid')).toBe('true');
    expect(field.getAttribute('aria-describedby')).toContain('onboarding-project-name-error');
    expect(createProject).not.toHaveBeenCalled();
  });

  it('keeps permission-denied controls natively disabled with an explanation', async () => {
    permitted.set(false);
    await fixture.whenStable();
    expect(requiredElement<HTMLInputElement>(root, '[name="name"]').disabled).toBe(true);
    expect(requiredElement<HTMLInputElement>(root, '[name="slug"]').disabled).toBe(true);
    const submit = requiredElement<HTMLButtonElement>(root, 'button[type="submit"]');
    expect(submit.disabled).toBe(true);
    expect(submit.getAttribute('aria-describedby')).toBe('onboarding-project-permission');
    expect(requiredElement(root, '#onboarding-project-permission').textContent?.trim()).not.toBe(
      '',
    );
  });

  it('hands native form submission to the persistent busy focus target', async () => {
    createProject.mockImplementation(() => new Promise(() => undefined));
    for (const name of ['name', 'slug']) {
      const field = requiredElement<HTMLInputElement>(root, `[name="${name}"]`);
      field.value = 'storefront-web';
      field.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await fixture.whenStable();
    const form = requiredElement<HTMLFormElement>(root, 'form');
    const submit = requiredElement<HTMLButtonElement>(root, 'button[type="submit"]');
    submit.focus();
    form.requestSubmit(submit);
    await fixture.whenStable();
    const status = requiredElement(root, '[data-testid="onboarding-project-busy"]');
    expect(createProject).toHaveBeenCalledTimes(1);
    expect(form.getAttribute('aria-busy')).toBe('true');
    expect(status.getAttribute('role')).toBe('status');
    expect(document.activeElement).toBe(status);
    expect(submit.disabled).toBe(true);
  });
  it('focuses a linked summary after a failed submit and never on correction', async () => {
    requiredElement<HTMLFormElement>(root, 'form').requestSubmit();
    await fixture.whenStable();
    const summary = requiredElement<HTMLElement>(root, '#onboarding-project-errors');
    expect(document.activeElement).toBe(summary);
    expect(summary.querySelectorAll('a')).toHaveLength(2);
    const link = requiredElement<HTMLAnchorElement>(summary, 'a[href="#onboarding-project-name"]');
    link.click();
    const name = requiredElement<HTMLInputElement>(root, '#onboarding-project-name');
    expect(document.activeElement).toBe(name);
    name.value = 'Storefront';
    name.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(document.activeElement).toBe(name);
    expect(summary.querySelector('a[href="#onboarding-project-name"]')).toBeNull();
    expect(createProject).not.toHaveBeenCalled();
  });

  it.each([320, 390, 767])(
    'keeps drafts read-only at %ipx and guards a stale desktop submit',
    async (width) => {
      for (const name of ['name', 'slug']) {
        const field = requiredElement<HTMLInputElement>(root, `[name="${name}"]`);
        field.value = 'retained-project';
        field.dispatchEvent(new Event('input', { bubbles: true }));
      }
      await fixture.whenStable();
      const form = requiredElement<HTMLFormElement>(root, 'form');
      viewport.setWidth(width);
      form.requestSubmit();
      await fixture.whenStable();
      expect(root.querySelector('form')).toBeNull();
      expect(root.querySelector('app-desktop-required-notice')?.textContent).toMatch(/desktop/i);
      expect(root.querySelector('dl')?.textContent).toContain('retained-project');
      expect(createProject).not.toHaveBeenCalled();
      viewport.setWidth(768);
      await fixture.whenStable();
      expect(requiredElement<HTMLInputElement>(root, '[name="name"]').value).toBe(
        'retained-project',
      );
      expect(requiredElement<HTMLInputElement>(root, '[name="slug"]').value).toBe(
        'retained-project',
      );
    },
  );
});
