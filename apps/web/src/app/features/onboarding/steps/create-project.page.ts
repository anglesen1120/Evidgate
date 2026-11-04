import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule, type NgForm } from '@angular/forms';
import { Router } from '@angular/router';
import type { CreateProjectRequest } from '@evidgate/sdk';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { createIntentRunner } from '../../../core/http/retry';
import { SessionStore } from '../../../core/session/session.store';
import {
  focusRefusalHeading,
  focusWaitingLine,
  requestRefusalFocus,
} from '../../../shared/a11y/step-focus';
import { RequestStateComponent } from '../../../shared/ui/states';
import { OnboardingStore } from '../onboarding.store';
import { sanitizeLabel } from './default-policy';
import { describedBy, fieldDetails, reasonFor } from './field-details';
import {
  NO_DISMISSED_REASONS,
  dismissFieldReason,
  showsFieldReason,
  type DismissedReasons,
} from './server-reasons';

@Component({
  selector: 'app-onboarding-create-project',
  imports: [FormsModule, RequestStateComponent, HlmAlertImports, HlmButton, HlmInput, HlmLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-5)' },
  template: `
    <form
      class="grid min-w-0 gap-(--eg-space-5) text-sm text-muted-foreground [&_p]:m-0"
      #form="ngForm"
      ngNativeValidate
      (ngSubmit)="submit(form)"
      [attr.aria-busy]="busy()"
    >
      <!-- The request disables both fields and the submit, so the pressed control cannot keep
           focus: the page moves it here first, which is also where the waiting state is spoken. -->
      <p
        class="min-h-(--eg-leading-sm) focus:outline-solid focus:outline-(length:--eg-focus-width) focus:outline-offset-(--eg-focus-offset) focus:outline-ring"
        role="status"
        tabindex="-1"
        #busyStatus
        data-testid="onboarding-project-busy"
      >
        @if (busy()) {
          {{ creatingLabel }}
        }
      </p>
      <div class="grid min-w-0 gap-(--eg-space-3)">
        @let nameError =
          (nameField.touched || submitted()) && !name().trim() ? nameRequired : serverNameError();
        <label hlmLabel for="onboarding-project-name">{{ nameLabel }}</label>
        <input
          hlmInput
          class="min-h-(--eg-control-touch)"
          id="onboarding-project-name"
          data-testid="onboarding-project-name"
          name="name"
          type="text"
          autocomplete="off"
          required
          [attr.pattern]="nonBlankPattern"
          [(ngModel)]="name"
          #nameField="ngModel"
          (input)="dismissServerReason('name')"
          (invalid)="submitted.set(true)"
          [disabled]="busy() || !permitted()"
          [forceInvalid]="nameError !== null"
          [attr.aria-invalid]="nameError === null ? null : 'true'"
          [aria-describedby]="
            describedBy(
              'onboarding-project-name-help',
              nameError === null ? null : 'onboarding-project-name-error'
            )
          "
        />
        <p id="onboarding-project-name-help">{{ nameHint }}</p>
        <!-- A refused field is spoken once, by the details alert; its own live region would repeat
             the same sentence. When no alert renders (forbidden, not_found), the field keeps it. -->
        <p
          id="onboarding-project-name-error"
          class="text-destructive"
          [attr.aria-live]="serverNameError() !== null && showsEnvelopeDetails() ? null : 'polite'"
        >
          @if (nameError !== null) {
            {{ nameError }}
          }
        </p>
      </div>
      <div class="grid min-w-0 gap-(--eg-space-3)">
        @let slugError =
          (slugField.touched || submitted()) && !slug().trim() ? slugRequired : serverSlugError();
        <label hlmLabel for="onboarding-project-slug">{{ slugLabel }}</label>
        <input
          hlmInput
          class="min-h-(--eg-control-touch)"
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
          (input)="dismissServerReason('slug')"
          (invalid)="submitted.set(true)"
          [disabled]="busy() || !permitted()"
          [forceInvalid]="slugError !== null"
          [attr.aria-invalid]="slugError === null ? null : 'true'"
          [aria-describedby]="
            describedBy(
              'onboarding-project-slug-help',
              slugError === null ? null : 'onboarding-project-slug-error'
            )
          "
        />
        <p id="onboarding-project-slug-help">{{ slugHint }}</p>
        <p
          id="onboarding-project-slug-error"
          class="text-destructive"
          [attr.aria-live]="serverSlugError() !== null && showsEnvelopeDetails() ? null : 'polite'"
        >
          @if (slugError !== null) {
            {{ slugError }}
          }
        </p>
      </div>
      @if (!permitted()) {
        <p id="onboarding-project-permission">{{ permissionReason }}</p>
      }
      <div class="flex flex-wrap gap-(--eg-space-3)">
        <button
          hlmBtn
          class="h-auto min-h-(--eg-control-touch) max-w-full justify-self-start whitespace-normal wrap-anywhere"
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
      @if (showsEnvelopeDetails()) {
        <section
          class="grid min-w-0 gap-(--eg-space-3) text-sm [&_p]:m-0"
          data-testid="onboarding-project-failure"
        >
          <!-- Outside the alerts below: a refusal that replaced the submit hands focus here, and
               focusing an alert would read its body a second time. -->
          <h3
            class="m-0 text-lg text-foreground focus:outline-solid focus:outline-(length:--eg-focus-width) focus:outline-offset-(--eg-focus-offset) focus:outline-ring"
            id="onboarding-project-failure-heading"
            tabindex="-1"
            #failureHeadingEl
          >
            {{ failureHeading }}
          </h3>
          @if (
            failure.kind === 'rate_limited' ||
            (failure.kind === 'unavailable' && failure.retryAfterSeconds !== null)
          ) {
            <p class="text-destructive">{{ failure.message }}</p>
          }
          @if (details().length > 0) {
            <div
              hlmAlert
              [variant]="'destructive'"
              role="alert"
              data-testid="onboarding-project-details"
              aria-labelledby="onboarding-project-details-label"
            >
              <p id="onboarding-project-details-label" class="text-destructive">
                {{ detailsLabel }}
              </p>
              <ul class="text-destructive">
                @for (detail of details(); track $index) {
                  <li>{{ detail.path }} {{ detail.reason }}</li>
                }
              </ul>
            </div>
          }
        </section>
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
   * field this misses would reuse the previous key for a different payload.
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

  /** The envelope's field rows, so a refusal points at the control it names. */
  protected readonly details = computed(() =>
    // Sanitised before the field is read, so an invisible character cannot stop a row from
    // matching its control. The values are still rendered by interpolation only.
    fieldDetails(
      (this.creation.error()?.details ?? []).map((detail) =>
        detail.path === undefined
          ? { ...detail, reason: sanitizeLabel(detail.reason) }
          : { ...detail, path: sanitizeLabel(detail.path), reason: sanitizeLabel(detail.reason) },
      ),
    ),
  );
  /** The reasons the visitor has answered by editing a field; see `server-reasons.ts`. */
  private readonly dismissed = signal<DismissedReasons>(NO_DISMISSED_REASONS);
  /**
   * The reason `field` still shows, or `null`.
   *
   * A reason is cleared by the first edit of its own field — not by a render, a focus or a later
   * refill — because the listed refusal describes the values that were submitted, and the visitor
   * is the one who says a value is no longer the one they meant.
   */
  private serverReason(field: string): string | null {
    if (!showsFieldReason(this.dismissed(), this.creation.error(), field)) return null;
    return reasonFor(this.details(), field);
  }
  protected readonly serverNameError = computed(() => this.serverReason('name'));
  protected readonly serverSlugError = computed(() => this.serverReason('slug'));
  /** Clears one field's displayed reason as soon as the visitor edits it; the alert keeps its rows. */
  protected dismissServerReason(field: string): void {
    this.dismissed.set(dismissFieldReason(this.dismissed(), this.creation.error(), field));
  }

  /** Refusals that print their own envelope rows; the other kinds render a pattern without one. */
  protected readonly showsEnvelopeDetails = computed(() => {
    const failure = this.creation.error();
    return failure !== null && failure.kind !== 'forbidden' && failure.kind !== 'not_found';
  });
  protected readonly describedBy = describedBy;
  private readonly busyStatus = viewChild<ElementRef<HTMLElement>>('busyStatus');
  private readonly failureHeadingRef = viewChild<ElementRef<HTMLElement>>('failureHeadingEl');
  /**
   * Set when this page's own submit or retry failed: the render pass then focuses the refusal
   * heading, because the pressed submit is disabled and replaced, leaving the visitor nowhere.
   */
  private readonly announceFailure = signal(false);

  protected readonly nameLabel = $localize`:@@onboarding.create.name:Project name`;
  protected readonly nameHint = $localize`:@@onboarding.create.name-hint:Choose a name your team will recognise.`;
  protected readonly nameRequired = $localize`:@@onboarding.create.name-required:Enter a project name.`;
  protected readonly slugLabel = $localize`:@@onboarding.create.slug:Project slug`;
  protected readonly slugHint = $localize`:@@onboarding.create.slug-hint:Use a short URL name, for example my-project.`;
  protected readonly slugRequired = $localize`:@@onboarding.create.slug-required:Enter a project slug.`;
  protected readonly permissionReason = $localize`:@@onboarding.create.permission:Ask your organisation owner for permission to create projects.`;
  protected readonly createLabel = $localize`:@@onboarding.create.submit:Create project`;
  protected readonly creatingLabel = $localize`:@@onboarding.create.submitting:Creating project…`;
  protected readonly failureHeading = $localize`:@@onboarding.create.failure-heading:The project could not be created`;
  protected readonly detailsLabel = $localize`:@@onboarding.create.details-label:The server refused these values`;
  // HTML validation only: a whitespace-only value is rejected before the request leaves the page.
  // Bound as an attribute so the template-i18n rule never treats the regex as translatable text.
  protected readonly nonBlankPattern = '.*\\S.*';

  constructor() {
    this.destroyRef.onDestroy(() => this.creation.reset());
    // The refusal heading is the page's own focus target: outside every alert, so moving there
    // repeats nothing. The kind is decided before the ref is read, because an effect can run
    // before this component's DOM update and the ref may still be the heading being removed.
    focusRefusalHeading({
      request: this.announceFailure,
      heading: this.failureHeadingRef,
      shown: () => this.creation.error() !== null && this.showsEnvelopeDetails(),
    });
  }

  protected async submit(form: NgForm): Promise<void> {
    this.submitted.set(true);
    if (this.submitDisabled() || form.invalid || !this.name().trim() || !this.slug().trim()) return;
    const body: CreateProjectRequest = { name: this.name().trim(), slug: this.slug().trim() };
    // An unchanged body keeps the intent and its key: after an ambiguous network failure the
    // resource may already exist, and a fresh key would let the server create it twice. Only a
    // corrected payload abandons the key, which is why the decision lives here.
    if (this.draftChanged()) {
      this.creation.reset();
    }
    this.submittedBody.set(body);
    // The request disables the pressed submit, so the waiting line holds the place until the page
    // hands it on — to the new step on success, or to the refusal heading.
    focusWaitingLine(this.busyStatus);
    await this.creation.run();
    await this.finishCreation();
    requestRefusalFocus(this.announceFailure, this.creation.error());
  }

  protected async retry(): Promise<void> {
    if (!this.permitted()) return;
    focusWaitingLine(this.busyStatus);
    await this.creation.retry();
    await this.finishCreation();
    requestRefusalFocus(this.announceFailure, this.creation.error());
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
    // navigation rejected — it must not be left behind for a later visit, which would render a
    // derivation nobody read for it.
    const activated =
      this.router.url === originUrl && (await this.navigateToProject(orgId, project.id));
    if (!activated) {
      this.store.clearCreated();
    }
    // The opened step's focus is the wizard's own: the create marker cues it, and one page owns it.
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
