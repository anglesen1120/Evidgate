import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import type { OnboardingProgress } from '../data/onboarding-progress.service';

@Component({
  selector: 'app-onboarding-evidence-panel',
  imports: [HlmCardImports],
  host: { class: 'block min-w-0' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside
      hlmCard
      class="gap-(--eg-space-4) p-(--eg-space-6)"
      data-testid="onboarding-evidence-panel"
      aria-labelledby="onboarding-evidence-title"
    >
      <h2 hlmCardTitle id="onboarding-evidence-title">{{ title }}</h2>
      @if (progress(); as resources) {
        @if (resources.project; as createdProject) {
          <dl
            class="m-0 [&_dt]:text-sm [&_dt]:text-muted-foreground [&_dt:not(:first-child)]:mt-(--eg-space-4) [&_dd]:mx-0 [&_dd]:mb-0 [&_dd]:mt-(--eg-space-2) [&_dd]:[overflow-wrap:anywhere]"
          >
            <dt>{{ projectLabel }}</dt>
            <dd>{{ createdProject.name }}</dd>
            <dt>{{ identifierLabel }}</dt>
            <dd class="font-mono text-xs">{{ createdProject.id }}</dd>
            @if (resources.bindings.length) {
              <dt>{{ bindingsLabel }}</dt>
              <dd>{{ resources.bindings.length }}{{ resources.bindingsTruncated ? '+' : '' }}</dd>
            }
            @if (resources.tokens.length) {
              <dt>{{ tokensLabel }}</dt>
              <dd>{{ resources.tokens.length }}{{ resources.tokensTruncated ? '+' : '' }}</dd>
            }
            @if (resources.sampleRun; as run) {
              <dt>{{ sampleLabel }}</dt>
              <dd class="font-mono text-xs">{{ run.run_id }}</dd>
              @if (run.current_evidence_revision_id; as revision) {
                <dt>{{ evidenceLabel }}</dt>
                <dd class="font-mono text-xs">{{ revision }}</dd>
              }
            }
            @if (activation(); as active) {
              <dt>{{ activationLabel }}</dt>
              <dd class="font-mono text-xs">{{ active.policy_revision_id }}</dd>
              <dt>{{ activatedAtLabel }}</dt>
              <dd>{{ active.activated_at }}</dd>
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
    </aside>
  `,
})
export class EvidencePanelComponent {
  readonly progress = input<OnboardingProgress | null>(null);
  readonly refreshFailed = input(false);
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
  protected readonly activatedAtLabel = $localize`:@@onboarding.evidence.activated-at:Activated at`;
  protected readonly activationUnavailable = $localize`:@@onboarding.evidence.activation-unavailable:Activation details are unavailable with your current access.`;
}
