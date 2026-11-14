import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';

import type { ApiError } from '../../../core/http/api-error';
import {
  createFormFeedback,
  FieldFeedbackComponent,
  FieldRequirementComponent,
  FormErrorSummaryComponent,
  FormRequiredNoteComponent,
  type FormFieldDefinition,
} from '../../forms';
import { DesktopMutationDirective } from '../narrow/desktop-mutation.directive';
import { DesktopRequiredNoticeComponent } from '../narrow/desktop-required-notice.component';
import {
  EmptyPatternComponent,
  ErrorPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
} from '../states';

/** Render-only specimens for feedback tones, bare states, skeletons and form feedback. */
@Component({
  selector: 'app-gallery-states',
  imports: [
    HlmButton,
    HlmCard,
    HlmInput,
    HlmLabel,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    FormRequiredNoteComponent,
    FieldRequirementComponent,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-feedback-tones"
    >
      <h2
        id="gallery-feedback-tones"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.feedback.title"
      >
        Inline feedback tones
      </h2>
      <app-inline-feedback [tone]="'info'" [title]="infoTitle" [announcement]="'off'" />
      <app-inline-feedback
        [tone]="'warning'"
        [title]="warningTitle"
        [description]="warningDescription"
        [announcement]="'off'"
      />
      <app-inline-feedback
        [tone]="'destructive'"
        [title]="errorTitle"
        [description]="errorDescription"
        [actionLabel]="retryLabel"
        [announcement]="'off'"
        (action)="recordRetry()"
      />
      <app-inline-feedback
        [tone]="'success'"
        [title]="savedTitle"
        [description]="savedDescription"
        [announcement]="'off'"
      />
      <app-inline-feedback
        [tone]="'warning'"
        [title]="longWarningTitle"
        [description]="longWarningDescription"
        [announcement]="'off'"
      >
        <button feedbackAction hlmBtn [variant]="'outline'" type="button" (click)="recordRetry()">
          {{ longWarningAction }}
        </button>
      </app-inline-feedback>
      <output i18n="@@shared.gallery.feedback.retries"
        >Local example retries: {{ retryCount() }}</output
      >
    </section>

    <section
      class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-bare-states"
    >
      <h2
        id="gallery-bare-states"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.bare-states.title"
      >
        Bare states inside a card
      </h2>
      <section hlmCard class="min-w-0 gap-(--eg-space-5) p-(--eg-space-5)">
        <app-empty-pattern [bare]="true" [title]="emptyTitle" [description]="emptyDescription" />
        <app-error-pattern
          [bare]="true"
          [headingLevel]="3"
          [error]="networkError"
          (retry)="recordRetry()"
        />
      </section>
      <app-loading-pattern [bare]="true" [layout]="'form'" [label]="loadingLabel" />
      <app-loading-pattern [bare]="true" [layout]="'cards'" [label]="loadingLabel" />
    </section>

    <section
      class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-fields"
    >
      <h2
        id="gallery-fields"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.fields.title"
      >
        Field feedback and narrow notices
      </h2>
      <app-form-required-note [convention]="'mostly-optional'" />
      <form class="grid min-w-0 gap-3" novalidate (submit)="submitSpecimen($event)">
        <app-form-error-summary [summaryId]="'gallery-form-errors'" [feedback]="feedback" />
        <div class="grid min-w-0 gap-2">
          <label hlmLabel [for]="field.id">
            {{ field.label }} <app-field-requirement [required]="true" />
          </label>
          <input
            hlmInput
            type="text"
            [id]="field.id"
            [value]="value()"
            [forceInvalid]="field.invalid()"
            [aria-describedby]="field.describedBy() ?? null"
            (input)="editSpecimen($event)"
            (blur)="field.blur()"
          />
          <app-field-feedback [field]="field" />
        </div>
        <button hlmBtn type="submit">{{ submitLabel }}</button>
      </form>
      <app-desktop-required-notice />
      <p class="eg-type-body m-0 wrap-anywhere">{{ readableFact }}</p>
      <ng-container *appDesktopMutation>
        <button hlmBtn [variant]="'outline'" type="button" (click)="guardedMutation()">
          {{ editLabel }}
        </button>
      </ng-container>
    </section>
  `,
})
export class GalleryStatesComponent {
  protected readonly infoTitle = $localize`:@@shared.gallery.feedback.info:A recorded note stays visible after saving.`;
  protected readonly warningTitle = $localize`:@@shared.gallery.feedback.warning:This example keeps an unsaved draft.`;
  protected readonly warningDescription = $localize`:@@shared.gallery.feedback.warning-description:Nothing shown here is sent to a server.`;
  protected readonly errorTitle = $localize`:@@shared.gallery.feedback.error:Evidence could not be saved`;
  protected readonly errorDescription = $localize`:@@shared.gallery.feedback.error-description:The request did not reach the server. Your evidence has not been replaced.`;
  protected readonly savedTitle = $localize`:@@shared.gallery.feedback.success:Project connection saved`;
  protected readonly savedDescription = $localize`:@@shared.gallery.feedback.success-description:The recorded attempt keeps its original evidence.`;
  protected readonly retryLabel = $localize`:@@shared.gallery.feedback.retry:Try again`;
  protected readonly longWarningTitle = $localize`:@@shared.gallery.feedback.long-warning:Some supplied evidence was replaced before this review began`;
  protected readonly longWarningDescription = $localize`:@@shared.gallery.feedback.long-warning-description:Review every previously supplied attempt and its immutable revision before recording a new decision.`;
  protected readonly longWarningAction = $localize`:@@shared.gallery.feedback.long-action:Review every recorded attempt`;
  protected readonly emptyTitle = $localize`:@@shared.gallery.bare-states.empty-title:No evidence yet`;
  protected readonly emptyDescription = $localize`:@@shared.gallery.bare-states.empty-description:Open the feature tree to inspect the cases awaiting evidence.`;
  protected readonly loadingLabel = $localize`:@@shared.gallery.bare-states.loading:Loading the recorded examples`;
  protected readonly readableFact = $localize`:@@shared.gallery.fields.readable:A recorded run keeps its original evidence identifier.`;
  protected readonly editLabel = $localize`:@@shared.gallery.fields.edit:Edit project connection`;
  protected readonly submitLabel = $localize`:@@shared.gallery.fields.submit:Save project key`;
  protected readonly retryCount = signal(0);
  protected readonly networkError: ApiError = {
    kind: 'network',
    status: 0,
    code: 'network_error',
    message: $localize`:@@shared.gallery.bare-states.error-message:The request could not reach the server. Your evidence has not been replaced.`,
    requestId: null,
    retryable: true,
    details: [],
    retryAfterSeconds: null,
  };
  protected readonly value = signal('');
  private readonly problem = computed(() =>
    this.value().trim().length > 0
      ? null
      : $localize`:@@shared.gallery.fields.required-error:Enter the project key to continue.`,
  );
  protected readonly feedback = createFormFeedback([
    {
      id: 'gallery-specimen-project-key',
      label: $localize`:@@shared.gallery.fields.label:Project key`,
      hint: $localize`:@@shared.gallery.fields.hint:Use the key shown on the project connection card.`,
      error: this.problem,
    } satisfies FormFieldDefinition,
  ]);
  protected readonly field = this.feedback.fields[0]!;

  protected recordRetry(): void {
    this.retryCount.update((count) => count + 1);
  }

  protected editSpecimen(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value);
    this.field.refresh();
  }

  protected submitSpecimen(event: Event): void {
    event.preventDefault();
    this.feedback.submit();
  }

  protected guardedMutation(): void {
    this.retryCount.update((count) => count + 1);
  }
}
