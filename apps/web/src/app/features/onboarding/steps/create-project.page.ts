import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  computed,
  effect,
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
import {
  createFormFeedback,
  FieldFeedbackComponent,
  FormErrorSummaryComponent,
  FormRequiredNoteComponent,
} from '../../../shared/forms';
import {
  DesktopMutationDirective,
  DesktopRequiredNoticeComponent,
  NarrowMutationPolicy,
} from '../../../shared/ui/narrow';
import { RequestStateComponent } from '../../../shared/ui/states';
import { OnboardingStore } from '../onboarding.store';
import { fieldDetails, reasonFor } from './field-details';
import {
  NO_DISMISSED_REASONS,
  dismissFieldReason,
  showsFieldReason,
  type DismissedReasons,
} from './server-reasons';

@Component({
  selector: 'app-onboarding-create-project',
  imports: [
    FormsModule,
    RequestStateComponent,
    HlmAlertImports,
    HlmButton,
    HlmInput,
    HlmLabel,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-5)' },
  template: `
    <p
      class="m-0 min-h-(--eg-leading-sm) text-sm focus:outline-solid focus:outline-(length:--eg-focus-width) focus:outline-offset-(--eg-focus-offset) focus:outline-ring"
      role="status"
      tabindex="-1"
      #busyStatus
      data-testid="onboarding-project-busy"
    >
      @if (busy()) {
        {{ creatingLabel }}
      }
    </p>
    @if (permitted()) {
      <app-desktop-required-notice />
    }
    @if (policy.narrow()) {
      <dl class="m-0 grid min-w-0 gap-(--eg-space-4) text-sm [&_dd]:m-0 [&_dd]:wrap-anywhere">
        <div>
          <dt>{{ nameLabel }}</dt>
          <dd>{{ name() || notSetLabel }}</dd>
        </div>
        <div>
          <dt>{{ slugLabel }}</dt>
          <dd>{{ slug() || notSetLabel }}</dd>
        </div>
      </dl>
      @if (!permitted()) {
        <p class="m-0 text-sm">{{ permissionReason }}</p>
      }
    }
    <form
      *appDesktopMutation
      class="grid min-w-0 max-w-[65ch] gap-(--eg-space-5) text-sm text-muted-foreground [&_p]:m-0"
      #form="ngForm"
      novalidate
      (ngSubmit)="submit(form)"
      [attr.aria-busy]="busy()"
    >
      <app-form-required-note />
      <app-form-error-summary [summaryId]="'onboarding-project-errors'" [feedback]="feedback" />
      <div class="grid min-w-0 gap-(--eg-space-3)">
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
          (input)="dismissServerReason('name')"
          (blur)="nameField.blur()"
          [disabled]="busy() || !permitted()"
          [forceInvalid]="nameField.invalid()"
          [attr.aria-invalid]="nameField.ariaInvalid()"
          [aria-describedby]="nameField.describedBy()"
        />
        <app-field-feedback [field]="nameField" />
      </div>
      <div class="grid min-w-0 gap-(--eg-space-3)">
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
          (input)="dismissServerReason('slug')"
          (blur)="slugField.blur()"
          [disabled]="busy() || !permitted()"
          [forceInvalid]="slugField.invalid()"
          [attr.aria-invalid]="slugField.ariaInvalid()"
          [aria-describedby]="slugField.describedBy()"
        />
        <app-field-feedback [field]="slugField" />
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
        [retryEligible]="
          policy.canMutate() && permitted() && creation.retryEligible() && !draftChanged()
        "
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
                  <li>{{ detail.message }}</li>
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
  private readonly data = inject(OnboardingDataService);

  private readonly store = inject(OnboardingStore);

  private readonly session = inject(SessionStore);

  private readonly router = inject(Router);

  private readonly destroyRef = inject(DestroyRef);

  protected readonly policy = inject(NarrowMutationPolicy);

  readonly orgId = input.required<string>();
  protected readonly name = signal('');
  protected readonly slug = signal('');
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
    fieldDetails(this.creation.error()?.details ?? [], 'project'),
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
  // Native requiredness/pattern metadata remains; the shared summary owns failed-submit focus.
  protected readonly nonBlankPattern = '.*\\S.*';
  protected readonly notSetLabel = $localize`:@@onboarding.create.not-set:Not set`;
  protected readonly feedback = createFormFeedback([
    {
      id: 'onboarding-project-name',
      label: this.nameLabel,
      hint: this.nameHint,
      error: () => (!this.name().trim() ? this.nameRequired : this.serverNameError()),
    },
    {
      id: 'onboarding-project-slug',
      label: this.slugLabel,
      hint: this.slugHint,
      error: () => (!this.slug().trim() ? this.slugRequired : this.serverSlugError()),
    },
  ]);
  // Envelope rows already announce field reasons; hidden-kind refusals need the inline channel.
  protected readonly nameField = {
    ...this.feedback.fields[0]!,
    announceInline: computed(
      () => !this.showsEnvelopeDetails() && this.feedback.fields[0]!.announceInline(),
    ),
  };
  protected readonly slugField = {
    ...this.feedback.fields[1]!,
    announceInline: computed(
      () => !this.showsEnvelopeDetails() && this.feedback.fields[1]!.announceInline(),
    ),
  };

  constructor() {
    effect(() => {
      this.orgId();
      this.feedback.reset();
    });
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
    if (!this.policy.allowsMutation() || this.submitDisabled()) return;
    if (form.invalid || !this.name().trim() || !this.slug().trim()) {
      this.feedback.submit();
      return;
    }
    // A valid local submit owes no summary focus; a server refusal keeps its own focus contract.
    this.feedback.reset();
    this.nameField.blur();
    this.slugField.blur();
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
    if (!this.policy.allowsMutation() || !this.permitted()) return;
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
