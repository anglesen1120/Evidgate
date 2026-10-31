import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import type { CreateProjectRequest, ProjectDto } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { ApiRequestError, type ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { OnboardingStore } from '../onboarding.store';
import { CreateProjectPage } from './create-project.page';

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

function failure(overrides: Partial<ApiError> = {}): ApiRequestError {
  return new ApiRequestError({
    kind: 'network',
    status: 0,
    code: 'network_error',
    message: 'Connection interrupted.',
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: null,
    ...overrides,
  });
}

describe('CreateProjectPage', () => {
  let fixture: ComponentFixture<CreateProjectPage>;
  let root: HTMLElement;
  const permitted = signal(true);
  const createProject = vi.fn<
    (org: string, body: CreateProjectRequest, intent: Intent) => Promise<ProjectDto>
  >(() => Promise.resolve(PROJECT));
  const projectCreated = vi.fn<(project: ProjectDto) => Promise<void>>(() => Promise.resolve());
  const clearCreated = vi.fn<() => void>();
  const navigate = vi.fn<(commands: unknown[]) => Promise<boolean>>(() => Promise.resolve(true));

  beforeEach(async () => {
    permitted.set(true);
    createProject.mockReset().mockResolvedValue(PROJECT);
    projectCreated.mockReset().mockResolvedValue(undefined);
    clearCreated.mockReset();
    navigate.mockReset().mockResolvedValue(true);
    await TestBed.configureTestingModule({
      imports: [CreateProjectPage],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OnboardingDataService, useValue: { createProject } },
        { provide: OnboardingStore, useValue: { projectCreated, clearCreated } },
        { provide: SessionStore, useValue: { has: () => permitted() } },
        { provide: Router, useValue: { url: '/orgs/org-1/onboarding', navigate } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(CreateProjectPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  async function fill(name = 'Storefront web', slug = 'storefront-web'): Promise<void> {
    for (const [selector, value] of [
      ['[name="name"]', name],
      ['[name="slug"]', slug],
    ]) {
      const input = root.querySelector<HTMLInputElement>(selector!)!;
      input.value = value!;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await fixture.whenStable();
  }

  async function submit(): Promise<void> {
    root
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await fixture.whenStable();
  }

  function submitButton(): HTMLButtonElement {
    return root.querySelector<HTMLButtonElement>('[data-testid="onboarding-project-submit"]')!;
  }

  /**
   * The submit chain keeps going after the form event — create, adopt, navigate, marker — so a
   * stable fixture is not proof it finished. One macrotask runs every microtask it queued.
   */
  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
  }

  it('submits once, adopts the SDK project, then navigates to its resumable URL', async () => {
    await fill();
    await submit();
    expect(createProject).toHaveBeenCalledTimes(1);
    expect(createProject).toHaveBeenCalledWith(
      'org-1',
      { name: 'Storefront web', slug: 'storefront-web' },
      expect.objectContaining({ idempotencyKey: expect.any(String) as string }),
    );
    expect(projectCreated).toHaveBeenCalledWith(PROJECT);
    expect(navigate).toHaveBeenCalledWith(['/orgs', 'org-1', 'onboarding', PROJECT.id]);
    expect(projectCreated.mock.invocationCallOrder[0]).toBeLessThan(
      navigate.mock.invocationCallOrder[0]!,
    );
    // The navigation did happen, so the marker stays armed for the activation it causes.
    expect(clearCreated).not.toHaveBeenCalled();
  });

  it('clears the create marker when the user navigated elsewhere before the read completed', async () => {
    const router = TestBed.inject(Router);
    projectCreated.mockImplementationOnce(() => {
      Object.defineProperty(router, 'url', { value: '/orgs/org-1/overview', configurable: true });
      return Promise.resolve();
    });
    await fill();
    await submit();
    expect(projectCreated).toHaveBeenCalledWith(PROJECT);
    expect(navigate).not.toHaveBeenCalled();
    expect(clearCreated).toHaveBeenCalledTimes(1);
  });

  it('clears the create marker when the router declines the navigation', async () => {
    // A guard that answers `false` activated nothing: no activation is coming for the marker.
    navigate.mockResolvedValueOnce(false);
    await fill();
    await submit();
    await settle();
    expect(projectCreated).toHaveBeenCalledWith(PROJECT);
    expect(navigate).toHaveBeenCalledWith(['/orgs', 'org-1', 'onboarding', PROJECT.id]);
    expect(clearCreated).toHaveBeenCalledTimes(1);
  });

  it('clears the create marker when the navigation rejects, and swallows the rejection', async () => {
    navigate.mockRejectedValueOnce(new Error('the guard threw'));
    await fill();
    await submit();
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/orgs', 'org-1', 'onboarding', PROJECT.id]);
    expect(clearCreated).toHaveBeenCalledTimes(1);
  });

  it('reuses the same Idempotency intent and body on explicit retry after a failure', async () => {
    createProject.mockRejectedValueOnce(failure());
    await fill();
    await submit();
    expect(root.textContent).toContain('Connection interrupted.');
    const retry = root.querySelector<HTMLButtonElement>('app-error-pattern button')!;
    expect(retry.disabled).toBe(false);
    retry.click();
    await fixture.whenStable();
    expect(createProject).toHaveBeenCalledTimes(2);
    expect(createProject.mock.calls[1]?.[2]).toBe(createProject.mock.calls[0]?.[2]);
    expect(createProject.mock.calls[1]?.[1]).toBe(createProject.mock.calls[0]?.[1]);
    expect(projectCreated).toHaveBeenCalledTimes(1);
  });

  it.each(['', '   '])('blocks an empty or whitespace-only name (%j)', async (name) => {
    await fill(name);
    await submit();
    expect(createProject).not.toHaveBeenCalled();
    expect(root.querySelector('#onboarding-project-name-error')?.textContent).toContain(
      'Enter a project name.',
    );
    expect(root.querySelector('[name="name"]')?.getAttribute('aria-invalid')).toBe('true');
    const input = root.querySelector<HTMLInputElement>('[name="name"]')!;
    expect(input.required).toBe(true);
    expect(root.querySelector<HTMLFormElement>('form')?.noValidate).toBe(false);
  });

  it('blocks an empty required slug and associates both fields with their labels', async () => {
    await fill('Storefront web', '');
    await submit();
    expect(createProject).not.toHaveBeenCalled();
    expect(root.querySelector('#onboarding-project-slug-error')?.textContent).toContain(
      'Enter a project slug.',
    );
    for (const name of ['name', 'slug']) {
      const input = root.querySelector<HTMLInputElement>(`[name="${name}"]`)!;
      expect(root.querySelector(`label[for="${input.id}"]`)).not.toBeNull();
    }
  });

  it('prevents a second request and disables the form while the first is pending', async () => {
    // `Promise.withResolvers` is ES2024 and the project compiles against ES2022.
    let resolvePending: (project: ProjectDto) => void = () => undefined;
    const pending = new Promise<ProjectDto>((resolve) => {
      resolvePending = resolve;
    });
    createProject.mockImplementationOnce(() => pending);
    await fill();
    await submit();
    expect(submitButton().disabled).toBe(true);
    expect(root.querySelector<HTMLInputElement>('[name="name"]')?.disabled).toBe(true);
    await submit();
    expect(createProject).toHaveBeenCalledTimes(1);
    resolvePending(PROJECT);
    await fixture.whenStable();
  });

  it('links a field refusal to its control, and only while that refusal is shown', async () => {
    createProject.mockRejectedValueOnce(
      failure({
        kind: 'validation',
        status: 422,
        message: 'The payload was refused.',
        details: [{ path: 'name', reason: 'A project with this name already exists.' }],
      }),
    );
    await fill();
    await submit();
    await settle();
    const input = root.querySelector<HTMLInputElement>('[name="name"]')!;
    const error = root.querySelector('#onboarding-project-name-error')!;
    expect(error.textContent).toContain('A project with this name already exists.');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toContain('onboarding-project-name-error');
    // The details alert speaks the same sentence; the field's own live region must stay off.
    expect(error.hasAttribute('aria-live')).toBe(false);
    const alert = root.querySelector<HTMLElement>('[data-testid="onboarding-project-details"]')!;
    expect(alert.getAttribute('role')).toBe('alert');
    const label = alert.getAttribute('aria-labelledby')!;
    expect(root.querySelector(`#${label}`)?.textContent).toContain('The server refused');
    // Nothing described may point outside the document once the refusal is replaced.
    createProject.mockResolvedValueOnce(PROJECT);
    await submit();
    await settle();
    for (const id of (input.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean)) {
      expect(root.querySelector(`#${id}`), `${id} must be rendered`).not.toBeNull();
    }
  });

  it('clears server field error on edit', async () => {
    createProject.mockRejectedValueOnce(
      failure({
        kind: 'validation',
        status: 422,
        details: [
          { path: 'name', reason: 'A project with this name already exists.' },
          { path: 'slug', reason: 'That slug is reserved.' },
        ],
      }),
    );
    await fill();
    await submit();
    await settle();
    const name = root.querySelector<HTMLInputElement>('[name="name"]')!;
    const slug = root.querySelector<HTMLInputElement>('[name="slug"]')!;
    name.value = 'Storefront web 2';
    name.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    // The edited field answers for itself: its reason and its invalidity go, the other field's stay.
    expect(name.getAttribute('aria-invalid')).toBeNull();
    expect(name.getAttribute('aria-describedby')).toBe('onboarding-project-name-help');
    expect(root.querySelector('#onboarding-project-name-error')?.textContent?.trim()).toBe('');
    expect(slug.getAttribute('aria-invalid')).toBe('true');
    expect(slug.getAttribute('aria-describedby')).toContain('onboarding-project-slug-error');
    expect(root.querySelector('#onboarding-project-slug-error')?.textContent).toContain(
      'That slug is reserved.',
    );
    // The alert keeps the rows it announced: it describes the values that were submitted.
    const alert = root.querySelector<HTMLElement>('[data-testid="onboarding-project-details"]')!;
    expect(alert.textContent).toContain('A project with this name already exists.');
    // An edited field that is still invalid stays invalid, now for its own reason.
    name.value = '   ';
    name.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(name.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#onboarding-project-name-error')?.textContent).toContain(
      'Enter a project name.',
    );
  });

  it('removes the invisible characters an envelope row carries before rendering it', async () => {
    createProject.mockRejectedValueOnce(
      failure({
        kind: 'validation',
        status: 422,
        details: [
          { path: '/name\u202E', reason: 'A\u0007 project with this name already exists.' },
          { path: '/slug', reason: 'Taken.\u202C' },
        ],
      }),
    );
    await fill();
    await submit();
    await settle();
    const rendered =
      root.querySelector('[data-testid="onboarding-project-details"]')?.textContent ?? '';
    expect(rendered).toContain('A project with this name already exists.');
    expect(rendered).toContain('Taken.');
    expect(rendered).not.toMatch(/[\p{Cc}\p{Cf}]/u);
    // The path was cleaned before the field was read, so the row still reaches its control.
    const nameError = root.querySelector('#onboarding-project-name-error')!;
    expect(nameError.textContent).toContain('A project with this name already exists.');
    expect(nameError.hasAttribute('aria-live')).toBe(false);
  });

  it.each([
    ['forbidden', 403],
    ['not_found', 404],
  ] as const)(
    'keeps the field live region when the %s refusal renders no details alert',
    async (kind, status) => {
      createProject.mockRejectedValueOnce(
        failure({
          kind,
          status,
          retryable: false,
          details: [{ path: 'name', reason: 'No project by that name.' }],
        }),
      );
      await fill();
      await submit();
      await settle();
      expect(root.querySelector('[data-testid="onboarding-project-details"]')).toBeNull();
      expect(root.querySelector('#onboarding-project-failure-heading')).toBeNull();
      const error = root.querySelector('#onboarding-project-name-error')!;
      expect(error.textContent).toContain('No project by that name.');
      expect(error.getAttribute('aria-live')).toBe('polite');
      // No heading renders, so the waiting line keeps the place and nothing queues a focus move.
      const busy = root.querySelector('[data-testid="onboarding-project-busy"]');
      expect(document.activeElement).toBe(busy);
      await settle();
      await fixture.whenStable();
      expect(document.activeElement).toBe(busy);
    },
  );

  it('does not hand focus to the heading a hidden-kind refusal removes', async () => {
    createProject.mockRejectedValueOnce(
      failure({
        kind: 'validation',
        status: 422,
        details: [{ path: 'slug', reason: 'This slug is taken.' }],
      }),
    );
    await fill();
    await submit();
    await settle();
    expect(document.activeElement).toBe(root.querySelector('#onboarding-project-failure-heading'));
    // The next refusal renders no heading, so the one already in the DOM is on its way out.
    createProject.mockRejectedValueOnce(failure({ kind: 'forbidden', status: 403 }));
    await submit();
    await settle();
    expect(root.querySelector('#onboarding-project-failure-heading')).toBeNull();
    expect(document.activeElement).toBe(
      root.querySelector('[data-testid="onboarding-project-busy"]'),
    );
  });

  it('holds focus on the waiting line while the request runs, then on the refusal heading', async () => {
    let rejectPending: (error: unknown) => void = () => undefined;
    const pending = new Promise<ProjectDto>((_resolve, reject) => {
      rejectPending = reject;
    });
    createProject.mockImplementationOnce(() => pending);
    await fill();
    await submit();
    expect(document.activeElement).toBe(
      root.querySelector('[data-testid="onboarding-project-busy"]'),
    );
    rejectPending(
      failure({
        kind: 'validation',
        status: 422,
        details: [{ path: 'slug', reason: 'This slug is taken.' }],
      }),
    );
    await settle();
    await fixture.whenStable();
    const heading = root.querySelector('#onboarding-project-failure-heading')!;
    expect(document.activeElement).toBe(heading);
    // The template ref must not shadow the label: the heading shows its text, not an element.
    expect(heading.textContent?.trim()).toBe('The project could not be created');
  });

  it('retains visible disabled mutation controls when permission is missing', async () => {
    permitted.set(false);
    await fixture.whenStable();
    expect(submitButton().disabled).toBe(true);
    expect(root.querySelector<HTMLInputElement>('[name="name"]')?.disabled).toBe(true);
    expect(root.textContent).toContain('Ask your organisation owner');
    await submit();
    expect(createProject).not.toHaveBeenCalled();
  });

  it.each([
    ['conflict', 409],
    ['validation', 422],
  ] as const)(
    'shows the %s server message and details verbatim without automatic retry',
    async (kind, status) => {
      createProject.mockRejectedValueOnce(
        failure({
          kind,
          status,
          retryable: false,
          message: 'The server refused this exact project name.',
          details: [{ path: 'name', reason: 'Already reserved by another operation.' }],
        }),
      );
      await fill();
      await submit();
      expect(root.querySelector('app-error-pattern')).not.toBeNull();
      expect(root.textContent).toContain('The server refused this exact project name.');
      expect(root.textContent).toContain('Already reserved by another operation.');
      expect(createProject).toHaveBeenCalledTimes(1);
      expect(projectCreated).not.toHaveBeenCalled();
    },
  );

  it('renders 429 through the shared Retry-After pattern and refuses early retry', async () => {
    createProject.mockRejectedValueOnce(
      failure({
        kind: 'rate_limited',
        status: 429,
        message: 'Slow down this project creation.',
        retryAfterSeconds: 999,
      }),
    );
    await fill();
    await submit();
    expect(root.querySelector('app-retry-after')).not.toBeNull();
    expect(root.textContent).toContain('Slow down this project creation.');
    const retry = root.querySelector<HTMLButtonElement>('app-retry-after button')!;
    expect(retry.disabled).toBe(true);
    expect(submitButton().disabled).toBe(true);
    retry.click();
    await fixture.whenStable();
    expect(createProject).toHaveBeenCalledTimes(1);
  });

  it('mints a different intent only after the user changes the failed payload', async () => {
    createProject.mockRejectedValueOnce(failure());
    await fill();
    await submit();
    await fill('Different project', 'different-project');
    await submit();
    expect(createProject).toHaveBeenCalledTimes(2);
    expect(createProject.mock.calls[1]?.[2].idempotencyKey).not.toBe(
      createProject.mock.calls[0]?.[2].idempotencyKey,
    );
    expect(createProject.mock.calls[1]?.[1]).toEqual({
      name: 'Different project',
      slug: 'different-project',
    });
  });

  it('keeps the intent when the user edits the draft and reverts to the submitted body', async () => {
    // An ambiguous network failure: the request may have reached the server, so resubmitting the
    // identical body must replay the same Idempotency-Key instead of risking a second project.
    createProject.mockRejectedValueOnce(failure());
    await fill();
    await submit();
    await fill('Different project', 'different-project');
    await fill();
    await submit();
    expect(createProject).toHaveBeenCalledTimes(2);
    expect(createProject.mock.calls[1]?.[2]).toBe(createProject.mock.calls[0]?.[2]);
    expect(createProject.mock.calls[1]?.[1]).toEqual({
      name: 'Storefront web',
      slug: 'storefront-web',
    });
  });

  it('renders forbidden through the refusal pattern while leaving the create action visible and disabled', async () => {
    createProject.mockRejectedValueOnce(
      failure({ kind: 'forbidden', status: 403, retryable: false }),
    );
    await fill();
    await submit();
    expect(root.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(submitButton().disabled).toBe(true);
  });
});
