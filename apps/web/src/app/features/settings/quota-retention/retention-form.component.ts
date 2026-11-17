import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  linkedSignal,
  viewChild,
} from '@angular/core';
import {
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  type AbstractControl,
  type ValidationErrors,
} from '@angular/forms';
import type { RetentionPolicy } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { FieldFeedbackComponent } from '../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import { FormRequiredNoteComponent } from '../../../shared/forms/form-requirement.component';
import {
  DesktopMutationDirective,
  DesktopRequiredNoticeComponent,
  NarrowMutationPolicy,
} from '../../../shared/ui/narrow';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';

import { RETENTION_COPY } from './retention-form-copy';
import { RetentionStore } from './retention.store';

type RetentionField = keyof RetentionPolicy;
type RetentionForm = FormGroup<{
  artifact_days: FormControl<number | null>;
  metadata_days: FormControl<number | null>;
}>;

const integer = (control: AbstractControl): ValidationErrors | null =>
  control.value === null || Number.isInteger(control.value) ? null : { integer: true };

// `Validators.required` is a method reference this config reads as an unbound method; a window
// field is required when the number accessor reports no value at all.
const required = (control: AbstractControl): ValidationErrors | null =>
  control.value === null ? { required: true } : null;

const orderedWindows = (group: AbstractControl): ValidationErrors | null => {
  const artifact: unknown = group.get('artifact_days')?.value;
  const metadata: unknown = group.get('metadata_days')?.value;
  return typeof artifact === 'number' && typeof metadata === 'number' && metadata < artifact
    ? { orderedWindows: true }
    : null;
};

@Component({
  selector: 'app-retention-form',
  imports: [
    ReactiveFormsModule,
    HlmButton,
    HlmCardImports,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    NumberValueComponent,
  ],
  providers: [RetentionStore],
  templateUrl: './retention-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'mt-6 block min-w-0 wrap-anywhere' },
})
export class RetentionFormComponent {
  protected readonly store = inject(RetentionStore);

  protected readonly mutationPolicy = inject(NarrowMutationPolicy);

  protected readonly copy = RETENTION_COPY;
  protected readonly sectionSize = 'section' as const;
  protected readonly subsectionSize = 'subsection' as const;
  protected readonly loadingLayout = 'form' as const;
  protected readonly destructive = 'destructive' as const;
  protected readonly success = 'success' as const;
  protected readonly projectLabel = (value: unknown): string =>
    this.store.session.projects().find((row) => row.id === value)?.name ?? this.copy.project;
  protected readonly form = linkedSignal(() => {
    const policy = this.store.policy();
    return new FormGroup(
      {
        artifact_days: new FormControl<number | null>(policy?.artifact_days ?? null, [
          required,
          integer,
        ]),
        metadata_days: new FormControl<number | null>(policy?.metadata_days ?? null, [
          required,
          integer,
        ]),
      },
      { validators: orderedWindows },
    );
  });
  protected readonly feedback = linkedSignal(() => {
    const form = this.form();
    return createFormFeedback([
      {
        id: 'retention-artifact',
        label: this.copy.artifact,
        describedBy: 'retention-hint',
        error: () => this.fieldError('artifact_days', form),
      },
      {
        id: 'retention-metadata',
        label: this.copy.metadata,
        describedBy: 'retention-hint',
        error: () => this.fieldError('metadata_days', form),
      },
    ]);
  });
  protected readonly artifactField = computed(() => this.feedback().fields[0]);
  protected readonly metadataField = computed(() => this.feedback().fields[1]);
  private readonly lastAttempt = linkedSignal(() => {
    this.form();
    return null as RetentionPolicy | null;
  });
  protected readonly pending = linkedSignal({
    source: () => ({ scope: this.store.scope(), policy: this.store.policy() }),
    computation: (): RetentionPolicy | null => null,
  });
  protected readonly reductions = computed(() => {
    const old = this.store.policy();
    const next = this.pending();
    if (!old || !next) return [];
    return (['artifact_days', 'metadata_days'] as const)
      .filter((field) => next[field] < old[field])
      .map((field) => ({
        field,
        label: field === 'artifact_days' ? this.copy.artifact : this.copy.metadata,
        old: old[field],
        next: next[field],
      }));
  });
  constructor() {
    effect(() => {
      if (!this.mutationPolicy.canMutate()) this.pending.set(null);
    });
  }
  private readonly submitButton = viewChild<ElementRef<HTMLButtonElement>>('submitButton');
  private readonly dialog = viewChild<HlmDialog>('confirmationDialog');

  private fieldError(field: RetentionField, form: RetentionForm): string | null {
    const control = form.controls[field];
    if (control.invalid) return this.copy.integer;
    if (field === 'metadata_days' && form.hasError('orderedWindows')) return this.copy.crossRule;
    const error = this.store.writeError();
    if (
      error?.kind !== 'validation' ||
      control.value !== this.lastAttempt()?.[field] ||
      !error.details.some((detail) => detail.path === field || detail.path === `/${field}`)
    )
      return null;
    const bounds = error.details
      .filter(
        (detail) => detail.path?.startsWith('retention_limits.') && detail.path.endsWith(field),
      )
      .map((detail) => `${detail.path}: ${detail.reason}`);
    return [error.message, ...bounds].join(' · ');
  }

  protected refreshFeedback(): void {
    this.feedback().fields.forEach((field) => field.refresh());
  }

  protected selectProject(value: unknown): void {
    if (typeof value === 'string' && !this.store.saving()) this.store.selectProject(value);
  }

  protected async submit(): Promise<void> {
    if (!this.mutationPolicy.allowsMutation() || !this.store.writable() || this.store.saving())
      return;
    const form = this.form();
    form.markAllAsTouched();
    form.updateValueAndValidity();
    if (!this.feedback().submit()) return;
    const { artifact_days, metadata_days } = form.getRawValue();
    const old = this.store.policy();
    if (artifact_days === null || metadata_days === null || !old) return;
    const body = { artifact_days, metadata_days };
    if (artifact_days < old.artifact_days || metadata_days < old.metadata_days) {
      this.pending.set(body);
    } else {
      this.lastAttempt.set(body);
      if (!(await this.store.save(body)) && this.form() === form) this.feedback().submit();
    }
  }

  protected async confirm(): Promise<void> {
    const body = this.pending();
    const scope = this.store.scope();
    if (
      !this.mutationPolicy.allowsMutation() ||
      !this.store.writable() ||
      !body ||
      !scope ||
      this.store.saving()
    )
      return;
    const form = this.form();
    this.lastAttempt.set(body);
    const saved = await this.store.save(body);
    // A settled write can arrive after the user moved to another organization and opened that
    // organization's own warning; only the confirmation this write was opened for may go away.
    const current = this.store.scope();
    if (
      this.pending() === body &&
      current?.org === scope.org &&
      current.project === scope.project
    ) {
      this.pending.set(null);
      if (!saved && this.form() === form) this.feedback().submit();
    }
  }

  protected cancel(): void {
    if (!this.store.saving()) this.pending.set(null);
  }

  protected dialogStateChanged(state: 'open' | 'closed'): void {
    if (state !== 'closed') return;
    if (this.store.saving()) {
      // Spartan freezes `disableClose` when the dialog opens, so Escape still starts a close during
      // the confirmed write. Reopening it in the same tick keeps the dialog and its focus in place.
      this.dialog()?.open();
      return;
    }
    this.cancel();
  }

  protected restoreFocus(): void {
    this.submitButton()?.nativeElement.focus();
  }
}
