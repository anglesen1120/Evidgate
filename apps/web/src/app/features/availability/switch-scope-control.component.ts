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
import { HlmSelectImports } from '@evidgate/ui/select';

import type { ApiError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { DesktopMutationDirective } from '../../shared/ui/narrow/desktop-mutation.directive';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import {
  HEALING_SWITCH_COPY,
  modeLabel as healingModeLabel,
  scopeLabel,
} from './healing-switch.copy';
import type { SettableMode, SwitchScope } from './healing-switch.store';
import { reasonExceedsLimit } from './reason-limit';

/** `unavailable` is an observation, never a value this editor may offer. */
const SETTABLE_MODES: readonly SettableMode[] = ['enabled', 'shadow', 'disabled'];

function isSettableMode(value: unknown): value is SettableMode {
  return value === 'enabled' || value === 'shadow' || value === 'disabled';
}

/** One scope editor; the hierarchy owns stored facts, and viewport policy never grants access. */
@Component({
  selector: 'app-switch-scope-control',
  imports: [
    HlmButton,
    HlmCardImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    DesktopMutationDirective,
  ],
  templateUrl: './switch-scope-control.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
})
export class SwitchScopeControl {
  private readonly narrowPolicy = inject(NarrowMutationPolicy);

  private readonly session = inject(SessionStore);

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
  protected readonly subsectionHeadingSize = 'subsection' as const;
  protected readonly saveVariant = computed(() =>
    this.scope() === 'project' ? 'default' : 'outline',
  );

  protected readonly selectedMode = linkedSignal<HealingSwitchView | null, SettableMode>({
    source: this.row,
    computation: (row) => row?.mode ?? 'shadow',
  });
  protected readonly reason = signal('');

  /** Measured on the trimmed value, so trailing whitespace never blocks a save. */
  protected readonly reasonTooLong = computed(() =>
    reasonExceedsLimit(
      this.reason(),
      this.scope() === 'project' ? 'project' : 'tenant',
      this.session.currentProject()?.id ?? '',
    ),
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

  protected readonly title = computed(() => scopeLabel(this.scope(), this.projectName()));

  protected errorText(error: ApiError): string {
    return error.kind === 'payload_too_large' ? this.copy.reasonTooLarge : error.message;
  }

  protected modeChanged(value: unknown): void {
    if (!this.narrowPolicy.allowsMutation() || !this.writable() || this.saving()) return;
    if (isSettableMode(value)) this.selectedMode.set(value);
  }

  protected reasonChanged(event: Event): void {
    if (!this.narrowPolicy.allowsMutation() || !this.writable() || this.saving()) return;
    const value = (event.target as HTMLTextAreaElement | null)?.value ?? '';
    this.reason.set(value);
  }

  protected submit(): void {
    if (
      !this.narrowPolicy.allowsMutation() ||
      !this.writable() ||
      this.saving() ||
      this.reasonTooLong()
    ) {
      return;
    }
    const reason = this.reason().trim();
    this.submitted.emit({ mode: this.selectedMode(), reason: reason.length === 0 ? null : reason });
  }
}
