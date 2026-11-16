// The project facts the tab shows above the revisions: the activation mode, the revision in force
// and the project's head revision. Each read has its own state, so a refused activation never hides
// a readable head (nor the list below). One retry control serves both, because the page retries
// every read of the tab with one action.

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  output,
  viewChild,
} from '@angular/core';
import type { PolicyActivationStateDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { PolicyModeChipComponent } from './policy-mode-chip.component';
import type { PolicyReadView } from './policy-reads';

@Component({
  selector: 'app-policy-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCardImports,
    PolicyModeChipComponent,
    AbsoluteTimeComponent,
    CopyableIdentifierComponent,
  ],
  host: { class: 'block min-w-0' },
  template: `
    @if (activation().phase !== 'idle' || head().phase !== 'idle') {
      <section hlmCard class="gap-4 p-4" aria-labelledby="policy-summary-title" data-policy-summary>
        <h3 hlmCardTitle [size]="subsectionSize" id="policy-summary-title">{{ copy.title }}</h3>
        <dl
          class="m-0 grid gap-4 [&_dd]:m-0 [&_dd]:wrap-anywhere [&_dt]:text-xs [&_dt]:text-muted-foreground sm:grid-cols-2"
        >
          <div
            class="grid min-w-0 gap-1"
            data-policy-activation
            [attr.data-policy-activation-state]="activation().phase"
          >
            <dt>{{ copy.activation }}</dt>
            <dd>
              @switch (activation().phase) {
                @case ('loading') {
                  <p role="status">{{ copy.activationLoading }}</p>
                }
                @case ('empty') {
                  <p data-policy-activation-empty>{{ copy.activationEmpty }}</p>
                }
                @case ('forbidden') {
                  <p data-policy-activation-forbidden>{{ copy.activationForbidden }}</p>
                }
                @case ('error') {
                  <p>{{ copy.readFailed }}</p>
                }
                @default {
                  <div class="flex flex-wrap items-center gap-2">
                    <app-policy-mode-chip [mode]="activation().value?.mode" />
                    <app-copyable-identifier
                      data-policy-activation-revision
                      [value]="activationRevisionId()"
                      [label]="copy.activation"
                    />
                  </div>
                  @if (activatedAt(); as at) {
                    <p class="mt-1 text-muted-foreground">
                      {{ copy.activatedAt }}
                      <app-absolute-time [value]="at" />
                    </p>
                  }
                }
              }
            </dd>
          </div>
          <div
            class="grid min-w-0 gap-1"
            data-policy-head-read
            [attr.data-policy-head-state]="head().phase"
          >
            <dt>{{ copy.head }}</dt>
            <dd>
              @switch (head().phase) {
                @case ('loading') {
                  <p role="status">{{ copy.headLoading }}</p>
                }
                @case ('empty') {
                  <p data-policy-head-empty>{{ copy.headEmpty }}</p>
                }
                @case ('forbidden') {
                  <p data-policy-head-forbidden>{{ copy.headForbidden }}</p>
                }
                @case ('error') {
                  <p>{{ copy.readFailed }}</p>
                }
                @default {
                  <div class="flex flex-wrap items-center gap-2">
                    <strong data-policy-head-no [attr.data-revision-no]="head().value?.revision_no"
                      >{{ copy.revision }} {{ head().value?.revision_no }}</strong
                    >
                    <app-copyable-identifier [value]="headRevisionId()" [label]="copy.head" />
                  </div>
                }
              }
            </dd>
          </div>
        </dl>
        @if (readFailed() || busy()) {
          <div
            class="flex flex-wrap items-center gap-2"
            data-policy-reads-error
            [attr.data-policy-reads-blocked]="blocked()"
          >
            @if (readFailure(); as failure) {
              <p>
                <code>{{ failure.code }}</code> {{ failure.message }}
              </p>
            }
            @if (waiting()) {
              <p class="text-muted-foreground" data-policy-reads-waiting>{{ copy.waiting }}</p>
            }
            <!-- aria-disabled, never disabled: a disabled button drops focus to body, and this
                 control must stay focusable across the retry it started. -->
            <button
              #retryButton
              hlmBtn
              [variant]="'outline'"
              class="min-h-[var(--eg-control-touch)] whitespace-normal"
              type="button"
              data-policy-reads-retry
              [attr.aria-disabled]="blocked()"
              [attr.aria-busy]="busy()"
              (click)="requestRetry()"
            >
              {{ copy.retry }}
            </button>
          </div>
        }
      </section>
    }
  `,
})
export class PolicySummaryComponent {
  readonly activation = input.required<PolicyReadView<PolicyActivationStateDto>>();
  readonly head = input.required<PolicyReadView<PolicyRevisionSummaryDto>>();
  /**
   * True while any read of the tab — these two, or the list behind them — is in flight. The retry
   * is inert then, and it stays on screen so the press that started it keeps its focus.
   */
  readonly busy = input(false);
  /** True until a server `Retry-After` wait is over; nothing is retried before that deadline. */
  readonly waiting = input(false);
  /** Asks the page to re-run both reads; the one control that retries them. */
  readonly retry = output<void>();

  protected readonly subsectionSize = 'subsection' as const;
  private readonly retryButton = viewChild<ElementRef<HTMLButtonElement>>('retryButton');
  /** Set by a press of the retry control: the next failure hands the focus back to it. */
  private retryOwnedFocus = false;

  constructor() {
    // The control is on screen while a retry runs, so it normally keeps focus on its own. If the
    // region is ever rebuilt, the focus a press gave it must come back to it, never fall to `body`.
    effect(() => {
      if (!this.retryOwnedFocus) {
        return;
      }
      if (!this.readFailed() && !this.blocked()) {
        // The retry succeeded and the region is gone: no failure is left to hand the focus to.
        this.retryOwnedFocus = false;
        return;
      }
      if (this.blocked()) {
        return;
      }
      const button = this.retryButton()?.nativeElement;
      if (button !== undefined) {
        this.retryOwnedFocus = false;
        button.focus();
      }
    });
  }

  protected readonly activationRevisionId = computed(
    () => this.activation().value?.policy_revision_id ?? '',
  );
  protected readonly activatedAt = computed(() => this.activation().value?.activated_at ?? null);
  protected readonly headRevisionId = computed(() => this.head().value?.id ?? '');
  /** True when either read failed in a way a retry can address; a `403` is not one of them. */
  protected readonly readFailed = computed(
    () => this.activation().phase === 'error' || this.head().phase === 'error',
  );
  /** True while a retry would be a no-op: a read is out, or the server's wait has not passed. */
  protected readonly blocked = computed(() => this.busy() || this.waiting());
  /** The server's own refusal, shown once when either read failed; `null` when neither did. */
  protected readonly readFailure = computed(() => {
    const activation = this.activation();
    const head = this.head();
    if (activation.phase === 'error' && activation.error !== null) {
      return activation.error;
    }
    return head.phase === 'error' ? head.error : null;
  });

  protected readonly copy = {
    title: $localize`:@@settings.policy.summary-title:Project policy`,
    activation: $localize`:@@settings.policy.activation:Activation`,
    activationLoading: $localize`:@@settings.policy.activation-loading:Reading the activation…`,
    activationEmpty: $localize`:@@settings.policy.activation-empty:No activation: this project is not gated by a policy.`,
    activationForbidden: $localize`:@@settings.policy.activation-forbidden:You cannot read this project's activation.`,
    head: $localize`:@@settings.policy.head-revision:Head revision`,
    headLoading: $localize`:@@settings.policy.head-loading:Reading the head revision…`,
    headEmpty: $localize`:@@settings.policy.head-empty:No policy revisions yet.`,
    headForbidden: $localize`:@@settings.policy.head-forbidden:You cannot read this project's head revision.`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    activatedAt: $localize`:@@settings.policy.activated-at:Activated`,
    readFailed: $localize`:@@settings.policy.read-failed:The project's policy details could not be read.`,
    retry: $localize`:@@settings.policy.read-retry:Reload policy details`,
    waiting: $localize`:@@settings.policy.read-retry-waiting:Reload available after the wait`,
  };

  /** One press, one read: a press while blocked or waiting asks for nothing. */
  protected requestRetry(): void {
    if (this.blocked()) {
      return;
    }
    this.retryOwnedFocus = true;
    this.retry.emit();
  }
}
