import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormsModule, type NgForm } from '@angular/forms';
import { Router } from '@angular/router';
import type { CreateProjectRequest } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { createIntentRunner } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import { RequestStateComponent } from '../../../shared/ui/states';
import { OnboardingStore } from '../onboarding.store';

@Component({
  selector: 'app-onboarding-create-project',
  imports: [FormsModule, RequestStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../../shared/ui/states/state-panel.css'],
  styles: `
    form {
      display: grid;
      gap: var(--eg-space-5);
      min-inline-size: var(--eg-space-0);
    }
    .field {
      display: grid;
      gap: var(--eg-space-3);
      min-inline-size: var(--eg-space-0);
    }
    label {
      color: var(--eg-text);
      font-size: var(--eg-text-sm);
      font-weight: var(--eg-weight-semibold);
    }
    input {
      box-sizing: border-box;
      inline-size: 100%;
      min-inline-size: var(--eg-space-0);
      min-block-size: var(--eg-control-lg);
      padding: var(--eg-space-3) var(--eg-space-4);
      border: var(--eg-border-width) solid var(--eg-border-control);
      border-radius: var(--eg-radius-sm);
      background: var(--eg-surface);
      color: var(--eg-text);
      font: inherit;
    }
    input:focus-visible {
      outline: var(--eg-focus-width) solid var(--eg-focus);
      outline-offset: var(--eg-focus-offset);
    }
    input:disabled {
      background: var(--eg-neutral-bg);
      color: var(--eg-text-muted);
    }
    p {
      margin: var(--eg-space-0);
      color: var(--eg-text-muted);
      font-size: var(--eg-text-sm);
      line-height: var(--eg-leading-sm);
    }
    .validation {
      color: var(--eg-danger-fg);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--eg-space-3);
    }
    .primary:not(:disabled) {
      border-color: var(--eg-accent);
      background: var(--eg-accent);
      color: var(--eg-on-accent);
    }
    .primary:hover:not(:disabled),
    .primary:active:not(:disabled) {
      box-shadow: inset var(--eg-space-0) var(--eg-space-0) var(--eg-space-0) var(--eg-border-width)
        var(--eg-on-accent);
    }
    app-request-state {
      display: block;
      margin-block-start: var(--eg-space-5);
    }
    @media (pointer: coarse) {
      input,
      .primary {
        min-block-size: var(--eg-control-touch);
      }
    }
  `,
  template: `
    <form #form="ngForm" ngNativeValidate (ngSubmit)="submit(form)" [attr.aria-busy]="busy()">
      <div class="field">
        <label for="onboarding-project-name">{{ nameLabel }}</label>
        <input
          id="onboarding-project-name"
          data-testid="onboarding-project-name"
          name="name"
          type="text"
          autocomplete="off"
          required
          [attr.pattern]="nonBlankPattern"
          [(ngModel)]="name"
          #nameField="ngModel"
          (invalid)="submitted.set(true)"
          [disabled]="busy() || !permitted()"
          [attr.aria-invalid]="(nameField.touched || submitted()) && !name().trim() ? 'true' : null"
          [attr.aria-describedby]="'onboarding-project-name-help onboarding-project-name-error'"
        />
        <p id="onboarding-project-name-help">{{ nameHint }}</p>
        <p id="onboarding-project-name-error" class="validation" [attr.aria-live]="'polite'">
          @if ((nameField.touched || submitted()) && !name().trim()) {
            {{ nameRequired }}
          }
        </p>
      </div>
      <div class="field">
        <label for="onboarding-project-slug">{{ slugLabel }}</label>
        <input
          id="onboarding-project-slug"
          name="slug"
          type="text"
          autocomplete="off"
          [attr.autocapitalize]="'none'"
          spellcheck="false"
          required
          [attr.pattern]="nonBlankPattern"
          [(ngModel)]="slug"
          #slugField="ngModel"
          (invalid)="submitted.set(true)"
          [disabled]="busy() || !permitted()"
          [attr.aria-invalid]="(slugField.touched || submitted()) && !slug().trim() ? 'true' : null"
          [attr.aria-describedby]="'onboarding-project-slug-help onboarding-project-slug-error'"
        />
        <p id="onboarding-project-slug-help">{{ slugHint }}</p>
        <p id="onboarding-project-slug-error" class="validation" [attr.aria-live]="'polite'">
          @if ((slugField.touched || submitted()) && !slug().trim()) {
            {{ slugRequired }}
          }
        </p>
      </div>
      @if (!permitted()) {
        <p id="onboarding-project-permission">{{ permissionReason }}</p>
      }
      <div class="actions">
        <button
          class="state-panel__action primary"
          data-testid="onboarding-project-submit"
          type="submit"
          [disabled]="submitDisabled()"
          [attr.aria-describedby]="!permitted() ? 'onboarding-project-permission' : null"
        >
          {{ busy() ? creatingLabel : createLabel }}
        </button>
      </div>
    </form>
    @if (creation.error(); as failure) {
      <app-request-state
        [status]="'error'"
        [error]="failure"
        [retryEligible]="permitted() && creation.retryEligible() && !draftChanged()"
        [retryBusy]="busy()"
        (retry)="retry()"
      />
      @if (failure.kind !== 'forbidden' && failure.kind !== 'not_found') {
        @if (
          failure.kind === 'rate_limited' ||
          (failure.kind === 'unavailable' && failure.retryAfterSeconds !== null)
        ) {
          <p class="validation">{{ failure.message }}</p>
        }
        @for (detail of failure.details; track $index) {
          <p class="validation">{{ detail.path }} {{ detail.reason }}</p>
        }
      }
    }
  `,
})
export class CreateProjectPage {
  readonly orgId = input.required<string>();
  protected readonly name = signal('');
  protected readonly slug = signal('');
  protected readonly submitted = signal(false);
  private readonly data = inject(OnboardingDataService);
  private readonly store = inject(OnboardingStore);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  /**
   * The body the current Idempotency intent was submitted with; `null` before the first submit.
   * The operation always sends this — not the live draft — so a retry replays the payload the key
   * belongs to, and the payload is what decides whether a key may be reused (see `submit`).
   */
  private readonly submittedBody = signal<CreateProjectRequest | null>(null);

  // The shared runner mints createIntent() once per submit, reuses it on explicit retries,
  // honours Retry-After and never retries automatically. Keep the submitted body immutable.
  protected readonly creation = createIntentRunner((intent) => {
    const body = this.submittedBody();
    if (body === null) throw new Error('A project intent requires a submitted form.');
    return this.data.createProject(this.orgId(), body, intent);
  });
  protected readonly permitted = computed(
    () => this.session.has('project:create') && this.creation.error()?.kind !== 'forbidden',
  );
  protected readonly busy = computed(
    () => this.creation.status() === 'running' || this.creation.status() === 'succeeded',
  );
  /**
   * True when the draft no longer carries the body the current intent was submitted with, and
   * true before the first submit. Every field of `CreateProjectRequest` must be compared here: a
   * field this misses would reuse the previous key for a different payload (S71 QA-F1).
   */
  protected readonly draftChanged = computed(() => {
    const submitted = this.submittedBody();
    return (
      submitted === null ||
      submitted.name !== this.name().trim() ||
      submitted.slug !== this.slug().trim()
    );
  });
  protected readonly submitDisabled = computed(
    () =>
      this.busy() ||
      !this.permitted() ||
      // A corrected payload is a new action, so a wait on the previous one no longer blocks it.
      (this.creation.error()?.retryable === true &&
        !this.creation.retryEligible() &&
        !this.draftChanged()),
  );

  protected readonly nameLabel = $localize`:@@onboarding.create.name:Project name`;
  protected readonly nameHint = $localize`:@@onboarding.create.name-hint:Choose a name your team will recognise.`;
  protected readonly nameRequired = $localize`:@@onboarding.create.name-required:Enter a project name.`;
  protected readonly slugLabel = $localize`:@@onboarding.create.slug:Project slug`;
  protected readonly slugHint = $localize`:@@onboarding.create.slug-hint:Use a short URL name, for example my-project.`;
  protected readonly slugRequired = $localize`:@@onboarding.create.slug-required:Enter a project slug.`;
  protected readonly permissionReason = $localize`:@@onboarding.create.permission:Ask your organisation owner for permission to create projects.`;
  protected readonly createLabel = $localize`:@@onboarding.create.submit:Create project`;
  protected readonly creatingLabel = $localize`:@@onboarding.create.submitting:Creating project…`;
  // HTML validation only: a whitespace-only value is rejected before the request leaves the page.
  // Bound as an attribute so the template-i18n rule never treats the regex as translatable text.
  protected readonly nonBlankPattern = '.*\\S.*';

  constructor() {
    this.destroyRef.onDestroy(() => this.creation.reset());
  }

  protected async submit(form: NgForm): Promise<void> {
    this.submitted.set(true);
    if (this.submitDisabled() || form.invalid || !this.name().trim() || !this.slug().trim()) return;
    const body: CreateProjectRequest = { name: this.name().trim(), slug: this.slug().trim() };
    // An unchanged body keeps the intent and its key: after an ambiguous network failure the
    // resource may already exist, and a fresh key would let the server create it twice. Only a
    // corrected payload abandons the key (S71 QA-F1), which is why the decision lives here and
    // not in a per-keystroke handler.
    if (this.draftChanged()) {
      this.creation.reset();
    }
    this.submittedBody.set(body);
    await this.creation.run();
    await this.finishCreation();
  }

  protected async retry(): Promise<void> {
    if (!this.permitted()) return;
    await this.creation.retry();
    await this.finishCreation();
  }

  private async finishCreation(): Promise<void> {
    const project = this.creation.result();
    if (project === undefined || this.destroyRef.destroyed) return;
    const originUrl = this.router.url;
    const orgId = this.orgId();
    await this.store.projectCreated(project);
    // projectCreated reloads the shell (and can unmount this form). Still update the URL,
    // unless the user navigated elsewhere while that read was in flight.
    //
    // The marker `projectCreated` armed is only good for the activation this URL change causes.
    // When no such activation is coming — the user already left, the router declined, or the
    // navigation rejected — it must not be left behind for a later visit (N2/F10), which would
    // render a derivation nobody read for it.
    const activated =
      this.router.url === originUrl && (await this.navigateToProject(orgId, project.id));
    if (!activated) {
      this.store.clearCreated();
    }
  }

  /** Navigates to the created project's resumable URL; false when the router did not activate it. */
  private async navigateToProject(orgId: string, projectId: string): Promise<boolean> {
    try {
      return await this.router.navigate(['/orgs', orgId, 'onboarding', projectId]);
    } catch {
      // A rejected navigation (a guard threw, a redirect the router refused) activated nothing,
      // so it counts as "did not happen" rather than escaping as an unhandled rejection.
      return false;
    }
  }
}
