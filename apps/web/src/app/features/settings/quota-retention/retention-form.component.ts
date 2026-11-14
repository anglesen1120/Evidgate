import {
  ChangeDetectionStrategy,
  Component,
  computed,
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

import { RETENTION_COPY } from './retention-form-copy';
import { RetentionStore } from './retention.store';

type RetentionField = keyof RetentionPolicy;

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
  imports: [ReactiveFormsModule, HlmButton, HlmCardImports, HlmDialogImports, HlmInput, HlmLabel],
  providers: [RetentionStore],
  templateUrl: './retention-form.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'mt-6 block min-w-0 wrap-anywhere' },
})
export class RetentionFormComponent {
  protected readonly store = inject(RetentionStore);
  protected readonly copy = RETENTION_COPY;
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
  private readonly artifactInput = viewChild<ElementRef<HTMLInputElement>>('artifactInput');
  private readonly metadataInput = viewChild<ElementRef<HTMLInputElement>>('metadataInput');
  protected readonly invalidSubmit = linkedSignal(() => {
    this.store.policy();
    return false;
  });
  private readonly submitButton = viewChild<ElementRef<HTMLButtonElement>>('submitButton');
  private readonly dialog = viewChild<HlmDialog>('confirmationDialog');

  protected fieldError(field: RetentionField): string | null {
    const form = this.form();
    const control = form.controls[field];
    if (control.invalid && (control.touched || control.dirty)) return this.copy.integer;
    if (field === 'metadata_days' && form.hasError('orderedWindows')) return this.copy.crossRule;
    const error = this.store.writeError();
    if (
      error?.kind !== 'validation' ||
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

  protected async submit(): Promise<void> {
    const form = this.form();
    form.markAllAsTouched();
    if (form.invalid) {
      this.invalidSubmit.set(true);
      this.focusInvalid();
      return;
    }
    this.invalidSubmit.set(false);
    if (!this.store.writable() || this.store.saving()) return;
    const { artifact_days, metadata_days } = form.getRawValue();
    const old = this.store.policy();
    if (artifact_days === null || metadata_days === null || !old) return;
    const body = { artifact_days, metadata_days };
    if (artifact_days < old.artifact_days || metadata_days < old.metadata_days) {
      this.pending.set(body);
    } else if (!(await this.store.save(body)) && this.form() === form) {
      this.focusInvalid();
    }
  }

  private focusInvalid(): void {
    if (this.fieldError('artifact_days')) this.artifactInput()?.nativeElement.focus();
    else if (this.fieldError('metadata_days')) this.metadataInput()?.nativeElement.focus();
  }

  protected async confirm(): Promise<void> {
    const body = this.pending();
    const scope = this.store.scope();
    if (!body || !scope || this.store.saving()) return;
    await this.store.save(body);
    // A settled write can arrive after the user moved to another organization and opened that
    // organization's own warning; only the confirmation this write was opened for may go away.
    const current = this.store.scope();
    if (this.pending() === body && current?.org === scope.org && current.project === scope.project)
      this.pending.set(null);
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
