// The project facts the tab shows above the revisions: the activation mode, the revision in force
// and the project's head revision. Each read has its own state, so a refused activation never hides
// a readable head (nor the list below). One retry control serves both, because the page retries
// every read of the tab with one action.

import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import type { PolicyActivationStateDto, PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { PolicyModeChipComponent } from './policy-mode-chip.component';
import type { PolicyReadView } from './policy-reads';

@Component({
  selector: 'app-policy-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmCardImports, PolicyModeChipComponent],
  host: { class: 'block min-w-0' },
  template: `
    @if (activation().phase !== 'idle' || head().phase !== 'idle') {
      <section
        hlmCard
        class="mb-4 gap-4 p-4"
        aria-labelledby="policy-summary-title"
        data-policy-summary
      >
        <h3 hlmCardTitle id="policy-summary-title" class="m-0 text-base">{{ copy.title }}</h3>
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
                    <code class="font-mono" data-policy-activation-revision>{{
                      activationRevisionId()
                    }}</code>
                  </div>
                  @if (activatedAt(); as at) {
                    <p class="mt-1 text-muted-foreground">
                      {{ copy.activatedAt }}
                      <time [attr.datetime]="at">{{ formatDate(at) }}</time>
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
                    <code class="font-mono">{{ headRevisionId() }}</code>
                  </div>
                }
              }
            </dd>
          </div>
        </dl>
        @if (readFailed()) {
          <div class="flex flex-wrap items-center gap-2" data-policy-reads-error>
            @if (readFailure(); as failure) {
              <p>
                <code>{{ failure.code }}</code> {{ failure.message }}
              </p>
            }
            <button
              hlmBtn
              [variant]="'outline'"
              class="min-h-[var(--eg-control-touch)] whitespace-normal"
              type="button"
              data-policy-reads-retry
              (click)="retry.emit()"
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
  /** Asks the page to re-run both reads; the one control that retries them. */
  readonly retry = output<void>();

  private readonly locale = inject(LOCALE_ID);
  private readonly dates = new Intl.DateTimeFormat(this.locale, {
    dateStyle: 'medium',
    timeStyle: 'short',
  });

  protected readonly activationRevisionId = computed(
    () => this.activation().value?.policy_revision_id ?? '',
  );
  protected readonly activatedAt = computed(() => this.activation().value?.activated_at ?? null);
  protected readonly headRevisionId = computed(() => this.head().value?.id ?? '');
  /** True when either read failed in a way a retry can address; a `403` is not one of them. */
  protected readonly readFailed = computed(
    () => this.activation().phase === 'error' || this.head().phase === 'error',
  );
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
  };

  protected formatDate(value: string): string {
    return this.dates.format(new Date(value));
  }
}
