import { DatePipe, DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
} from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';

import { shortDateFormat } from '../../../shared/time/short-date-format';
import type { OnboardingProgress } from '../data/onboarding-progress.service';
import { activationInstantLabel } from '../steps/default-policy';

@Component({
  selector: 'app-onboarding-evidence-panel',
  imports: [DatePipe, DecimalPipe, HlmCardImports],
  host: { class: 'block min-w-0' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section
      hlmCard
      class="min-w-0 gap-(--eg-space-4) p-(--eg-space-6)"
      data-testid="onboarding-evidence-panel"
      aria-labelledby="onboarding-evidence-title"
    >
      <h2 hlmCardTitle [size]="sectionHeadingSize" id="onboarding-evidence-title">{{ title }}</h2>
      <p class="m-0 text-sm text-muted-foreground">{{ sourceLabel }}</p>
      @if (progress(); as resources) {
        @if (resources.project; as createdProject) {
          <dl
            class="m-0 grid min-w-0 gap-(--eg-space-5) min-[768px]:grid-cols-2 min-[1280px]:grid-cols-3 [&>div]:min-w-0 [&_dt]:text-sm [&_dt]:text-muted-foreground [&_dd]:mx-0 [&_dd]:mb-0 [&_dd]:mt-(--eg-space-2) [&_dd]:wrap-anywhere"
          >
            <div>
              <dt>{{ projectLabel }}</dt>
              <dd>{{ createdProject.name }}</dd>
            </div>
            <div>
              <dt>{{ identifierLabel }}</dt>
              <dd class="font-mono text-sm">
                <bdi>{{ createdProject.id }}</bdi>
              </dd>
            </div>
            @if (resources.bindings.length) {
              <div>
                <dt>{{ bindingsLabel }}</dt>
                <dd>
                  {{ resources.bindings.length | number
                  }}{{ resources.bindingsTruncated ? '+' : '' }}
                </dd>
              </div>
            }
            @if (resources.tokens.length) {
              <div>
                <dt>{{ tokensLabel }}</dt>
                <dd>
                  {{ resources.tokens.length | number }}{{ resources.tokensTruncated ? '+' : '' }}
                </dd>
              </div>
            }
            @if (resources.sampleRun; as run) {
              <div>
                <dt>{{ sampleLabel }}</dt>
                <dd class="font-mono text-sm">
                  <bdi>{{ run.run_id }}</bdi>
                </dd>
              </div>
              @if (run.current_evidence_revision_id; as revision) {
                <div>
                  <dt>{{ evidenceLabel }}</dt>
                  <dd class="font-mono text-sm">
                    <bdi>{{ revision }}</bdi>
                  </dd>
                </div>
              }
            }
            @if (activation(); as active) {
              <div>
                <dt>{{ activationLabel }}</dt>
                <dd class="font-mono text-sm">
                  <bdi>{{ active.policy_revision_id }}</bdi>
                </dd>
              </div>
              <div>
                <dt>{{ activatedAtLabel }}</dt>
                <dd>
                  @if (activatedAt(); as instant) {
                    <time [attr.datetime]="instant">{{
                      instant | date: dateTimeFormat : '+0000'
                    }}</time>
                  } @else {
                    {{ unavailable }}
                  }
                </dd>
              </div>
            }
          </dl>
          @if (resources.activation.kind === 'forbidden') {
            <p class="m-0 text-sm text-muted-foreground" [attr.data-resource-state]="'unavailable'">
              {{ activationUnavailable }}
            </p>
          }
        } @else {
          <p
            class="m-0 border-t border-border pt-(--eg-space-4) text-sm text-muted-foreground"
            [attr.data-resource-state]="'empty'"
          >
            {{ empty }}
          </p>
        }
      } @else {
        <p class="m-0 text-sm text-muted-foreground" [attr.data-resource-state]="'unavailable'">
          {{ unavailable }}
        </p>
      }
      @if (refreshFailed()) {
        <p
          class="m-0 text-sm text-muted-foreground"
          role="status"
          [attr.data-resource-state]="'stale'"
        >
          {{ stale }}
        </p>
      }
    </section>
  `,
})
export class EvidencePanelComponent {
  protected readonly dateTimeFormat = shortDateFormat(inject(LOCALE_ID), true, 'medium');

  readonly progress = input<OnboardingProgress | null>(null);
  readonly refreshFailed = input(false);
  protected readonly sectionHeadingSize = 'section' as const;
  protected readonly sourceLabel = $localize`:@@onboarding.evidence.source:Read from server-confirmed resources, not browser completion flags.`;
  protected readonly activatedAt = computed(() =>
    activationInstantLabel(this.activation()?.activated_at),
  );
  protected readonly activation = computed(() => {
    const activation = this.progress()?.activation;
    return activation?.kind === 'active' ? activation.state : null;
  });
  protected readonly title = $localize`:@@onboarding.evidence.title:Created resources`;
  protected readonly projectLabel = $localize`:@@onboarding.evidence.project:Project`;
  protected readonly identifierLabel = $localize`:@@onboarding.evidence.identifier:Project ID`;
  protected readonly empty = $localize`:@@onboarding.evidence.no-resources:No setup resources have been created yet.`;
  protected readonly unavailable = $localize`:@@onboarding.evidence.unavailable:Created resources are unavailable.`;
  protected readonly stale = $localize`:@@onboarding.evidence.stale:Resources could not be refreshed. Showing the last confirmed resources.`;
  protected readonly bindingsLabel = $localize`:@@onboarding.evidence.bindings:CI bindings`;
  protected readonly tokensLabel = $localize`:@@onboarding.evidence.tokens:Ingest tokens`;
  protected readonly sampleLabel = $localize`:@@onboarding.evidence.sample:Sample run ID`;
  protected readonly evidenceLabel = $localize`:@@onboarding.evidence.revision:Evidence revision ID`;
  protected readonly activationLabel = $localize`:@@onboarding.evidence.activation:Activated policy revision`;
  protected readonly activatedAtLabel = $localize`:@@onboarding.evidence.activated-at:Activated at (UTC)`;
  protected readonly activationUnavailable = $localize`:@@onboarding.evidence.activation-unavailable:Activation details are unavailable with your current access.`;
}
