import { FieldHintComponent } from '../../shared/ui/forms/field-hint.component';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { HealingSwitchView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardTitle } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
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
    FieldHintComponent,
    HlmButton,
    HlmCardTitle,
    HlmDialogImports,
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

  private readonly injector = inject(Injector);

  readonly scope = input.required<SwitchScope>();
  readonly row = input<HealingSwitchView | null>(null);
  readonly blockedBy = input<SwitchScope | null>(null);
  readonly editable = input(false);
  readonly saving = input(false);
  readonly error = input<ApiError | null>(null);
  readonly saved = input(false);
  readonly submitted = output<{
    mode: SettableMode;
    reason: string | null;
    action?: 'healing-kill-switch' | undefined;
  }>();

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

  protected readonly confirmOpen = signal(false);
  private readonly disableTrigger = viewChild<ElementRef<HTMLButtonElement>>('disableTrigger');
  private readonly scopeHeading = viewChild<ElementRef<HTMLElement>>('scopeHeading');
  /** Containment is the one mutation a narrow screen may send, so it is offered only there. */
  protected readonly containmentVisible = computed(
    () =>
      !this.narrowPolicy.canMutateFor() &&
      this.writable() &&
      this.narrowPolicy.canMutateFor('healing-kill-switch') &&
      this.row()?.mode !== 'disabled',
  );
  protected readonly disableTitle = computed(
    () =>
      $localize`:@@healingswitch.disable-scope-title:Disable healing for ${this.title()}:SCOPE:?`,
  );
  protected readonly disableConsequence = $localize`:@@healingswitch.disable-consequence:All shadow healing proposals for this scope will stop right away. Existing PRs and original failures stay as they are.`;
  protected readonly disableTenantImpact = $localize`:@@healingswitch.disable-tenant-impact:This stops healing proposals for all projects in this tenant.`;
  protected readonly disableHint = $localize`:@@healingswitch.disable-hint:Emergency stop only. Turning healing on or changing other modes needs a desktop screen.`;
  protected readonly disableButton = $localize`:@@healingswitch.disable:Disable healing`;
  protected readonly disableKeep = $localize`:@@healingswitch.disable-keep:Keep enabled`;
  protected readonly disableConfirm = $localize`:@@healingswitch.disable-confirm:Confirm disable`;

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

  protected readonly modeAdvisory = computed(() => this.blockedBy() === null && this.editable());
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

  /** Shared by every route that closes the dialog; a second close is a no-op. */
  protected closeConfirm(confirmed = false): void {
    if (!this.confirmOpen()) return;
    this.confirmOpen.set(false);
    this.restoreFocusAfterClose(confirmed);
  }

  /**
   * Cancel and Escape return focus to the containment trigger. After a confirmed containment the
   * readback removes that trigger, so focus goes to the stable scope heading instead (also the
   * fallback when a resize removed the trigger).
   */
  private restoreFocusAfterClose(confirmed: boolean): void {
    afterNextRender(
      () => {
        const trigger = this.disableTrigger()?.nativeElement;
        if (!confirmed && trigger !== undefined && trigger.isConnected && !trigger.disabled) {
          trigger.focus();
          return;
        }
        this.scopeHeading()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  private canSend(action?: 'healing-kill-switch'): boolean {
    return this.narrowPolicy.allowsMutation(action) && this.writable() && !this.saving();
  }

  /** Desktop save keeps the draft and its length check; containment never sends the draft. */
  protected submit(): void {
    if (!this.canSend() || this.reasonTooLong()) return;
    const reason = this.reason().trim();
    this.submitted.emit({ mode: this.selectedMode(), reason: reason.length === 0 ? null : reason });
  }

  /** Containment opens the confirm first; nothing is sent until the operator confirms. */
  protected openDisableConfirm(): void {
    if (!this.canSend('healing-kill-switch')) return;
    this.confirmOpen.set(true);
  }

  /** Single use: the confirm must be open, and closing it consumes that state before the emit. */
  protected confirmDisable(): void {
    if (!this.confirmOpen()) return;
    this.closeConfirm(true);
    if (!this.canSend('healing-kill-switch')) return;
    this.submitted.emit({ mode: 'disabled', reason: null, action: 'healing-kill-switch' });
  }

  protected dialogStateChanged(state: 'open' | 'closed'): void {
    if (state === 'closed') this.closeConfirm();
  }
}
