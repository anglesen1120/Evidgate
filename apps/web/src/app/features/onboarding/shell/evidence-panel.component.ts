import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { OnboardingProgress } from '../data/onboarding-progress.service';

@Component({
  selector: 'app-onboarding-evidence-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside data-testid="onboarding-evidence-panel" aria-labelledby="onboarding-evidence-title">
      <h2 id="onboarding-evidence-title">{{ title }}</h2>
      @if (progress(); as resources) {
        @if (resources.project; as createdProject) {
          <dl>
            <dt>{{ projectLabel }}</dt>
            <dd>{{ createdProject.name }}</dd>
            <dt>{{ identifierLabel }}</dt>
            <dd class="identifier">{{ createdProject.id }}</dd>
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
              <dd class="identifier">{{ run.run_id }}</dd>
              @if (run.current_evidence_revision_id; as revision) {
                <dt>{{ evidenceLabel }}</dt>
                <dd class="identifier">{{ revision }}</dd>
              }
            }
            @if (activation(); as active) {
              <dt>{{ activationLabel }}</dt>
              <dd class="identifier">{{ active.policy_revision_id }}</dd>
              <dt>{{ activatedAtLabel }}</dt>
              <dd>{{ active.activated_at }}</dd>
            }
          </dl>
          @if (resources.activation.kind === 'forbidden') {
            <p [attr.data-resource-state]="'unavailable'">{{ activationUnavailable }}</p>
          }
        } @else {
          <p class="empty" [attr.data-resource-state]="'empty'">{{ empty }}</p>
        }
      } @else {
        <p [attr.data-resource-state]="'unavailable'">{{ unavailable }}</p>
      }
      @if (refreshFailed()) {
        <p role="status" [attr.data-resource-state]="'stale'">{{ stale }}</p>
      }
    </aside>
  `,
  styles: `
    :host {
      display: block;
      min-inline-size: var(--eg-space-0);
    }
    aside {
      padding: var(--eg-space-6);
      border: var(--eg-border-width) solid var(--eg-border);
      border-radius: var(--eg-radius-md);
      background: var(--eg-surface);
      color: var(--eg-text);
    }
    h2 {
      margin: var(--eg-space-0);
      font-family: var(--eg-font-display);
      font-size: var(--eg-text-lg);
      line-height: var(--eg-leading-lg);
    }
    p,
    dt {
      color: var(--eg-text-muted);
      font-size: var(--eg-text-sm);
      line-height: var(--eg-leading-sm);
    }
    p,
    dl {
      margin: var(--eg-space-4) var(--eg-space-0) var(--eg-space-0);
    }
    dt:not(:first-child) {
      margin-block-start: var(--eg-space-4);
    }
    dd {
      margin: var(--eg-space-2) var(--eg-space-0) var(--eg-space-0);
      overflow-wrap: anywhere;
    }
    .identifier {
      overflow-wrap: anywhere;
      font-family: var(--eg-font-mono);
      font-size: var(--eg-text-xs);
      line-height: var(--eg-leading-xs);
    }
    .empty {
      padding-block-start: var(--eg-space-4);
      border-block-start: var(--eg-border-width) solid var(--eg-border);
    }
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
