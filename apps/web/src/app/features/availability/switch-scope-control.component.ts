import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { HealingSwitchView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';

import type { ApiError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { HEALING_SWITCH_COPY, modeLabel as healingModeLabel } from './healing-switch.copy';
import type { SettableMode, SwitchScope } from './healing-switch.store';

/** `unavailable` is an observation, never a value this editor may offer. */
const SETTABLE_MODES: readonly SettableMode[] = ['enabled', 'shadow', 'disabled'];
/** The reason limit in Unicode code points; `String.length` counts UTF-16 units and cuts emoji. */
const REASON_LIMIT = 2000;

function isSettableMode(value: string | undefined): value is SettableMode {
  return value === 'enabled' || value === 'shadow' || value === 'disabled';
}

/**
 * One scope of the healing switch: its stored row, and — for tenant and project only — the mode
 * editor. The system row is never writable from this page, and an ancestor that takes precedence
 * keeps the editor visible but disabled so the reason it cannot change stays on screen.
 */
@Component({
  selector: 'app-switch-scope-control',
  imports: [DatePipe, HlmButton, HlmCardImports, HlmInput, HlmLabel],
  templateUrl: './switch-scope-control.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
})
export class SwitchScopeControl {
  readonly scope = input.required<SwitchScope>();
  readonly row = input<HealingSwitchView | null>(null);
  readonly blockedBy = input<SwitchScope | null>(null);
  readonly editable = input(false);
  readonly saving = input(false);
  readonly error = input<ApiError | null>(null);
  readonly saved = input(false);
  readonly submitted = output<{ mode: SettableMode; reason: string | null }>();

  protected readonly copy = HEALING_SWITCH_COPY;
  protected readonly modeLabel = healingModeLabel;
  protected readonly modes = SETTABLE_MODES;
  private readonly session = inject(SessionStore);

  protected readonly selectedMode = linkedSignal<HealingSwitchView | null, SettableMode>({
    source: this.row,
    computation: (row) => row?.mode ?? 'shadow',
  });
  protected readonly reason = signal('');

  /** Measured on the trimmed value, so trailing whitespace never blocks a save. */
  protected readonly reasonTooLong = computed(
    () => [...this.reason().trim()].length > REASON_LIMIT,
  );

  protected readonly projectName = computed(() => this.session.currentProject()?.name ?? '');

  protected readonly writable = computed(
    () => this.scope() !== 'system' && this.editable() && this.blockedBy() === null,
  );

  protected readonly titleId = computed(() => `healing-switch-${this.scope()}-title`);
  protected readonly modeId = computed(() => `healing-switch-${this.scope()}-mode`);
  protected readonly reasonId = computed(() => `healing-switch-${this.scope()}-reason`);
  protected readonly noteId = computed(() => `healing-switch-${this.scope()}-note`);
  protected readonly reasonHintId = computed(() => `healing-switch-${this.scope()}-reason-hint`);
  protected readonly errorId = computed(() => `healing-switch-${this.scope()}-error`);
  protected readonly reasonTooLongId = computed(
    () => `healing-switch-${this.scope()}-reason-too-long`,
  );

  protected readonly note = computed(() => {
    if (this.scope() === 'system') return '';
    const ancestor = this.blockedBy();
    if (ancestor === 'system') return this.copy.blockedBySystem;
    if (ancestor !== null) return this.copy.blockedByTenant;
    return this.editable() ? this.copy.modeHint : this.copy.readOnly;
  });

  // `hlmInput` carries `aria-describedby` as an input whose host binding writes the attribute last,
  // so the template binds the input: an `attr.` binding here is overwritten with nothing.
  protected readonly describedBy = computed(() => {
    const ids: string[] = [];
    if (this.note().length > 0) ids.push(this.noteId());
    if (this.error() !== null) ids.push(this.errorId());
    return ids.length === 0 ? null : ids.join(' ');
  });

  protected readonly reasonDescribedBy = computed(() => {
    const ids: string[] = [this.reasonHintId()];
    if (this.reasonTooLong()) ids.push(this.reasonTooLongId());
    if (this.note().length > 0) ids.push(this.noteId());
    if (this.error() !== null) ids.push(this.errorId());
    return ids.join(' ');
  });

  protected title(): string {
    switch (this.scope()) {
      case 'system':
        return this.copy.scopeSystem;
      case 'tenant':
        return this.copy.scopeTenant;
      default: {
        const name = this.projectName();
        return name.length === 0 ? this.copy.scopeProject : `${this.copy.scopeProject}: ${name}`;
      }
    }
  }

  /** A null field is either a redacted system row or a value nobody recorded; say which. */
  protected fieldValue(value: string | null): string {
    if (value !== null) return value;
    return this.scope() === 'system' ? this.copy.redacted : this.copy.notProvided;
  }

  /** A malformed timestamp renders as an em dash instead of making `DatePipe` throw. */
  protected readonly changedAt = computed(() => {
    const stored = this.row();
    if (stored === null) return null;
    const at = new Date(stored.at);
    return Number.isNaN(at.getTime()) ? null : at;
  });

  protected modeChanged(event: Event): void {
    const value = (event.target as HTMLSelectElement | null)?.value;
    if (isSettableMode(value)) this.selectedMode.set(value);
  }

  protected reasonChanged(event: Event): void {
    const value = (event.target as HTMLTextAreaElement | null)?.value ?? '';
    this.reason.set(value);
  }

  protected submit(): void {
    if (!this.writable() || this.saving() || this.reasonTooLong()) return;
    const reason = this.reason().trim();
    this.submitted.emit({ mode: this.selectedMode(), reason: reason.length === 0 ? null : reason });
  }
}
