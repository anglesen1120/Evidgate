import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { ProjectDto } from '@evidgate/sdk';

import type { StepNumber } from '../data/step-derivation';

@Component({
  selector: 'app-onboarding-evidence-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside data-testid="onboarding-evidence-panel" aria-labelledby="onboarding-evidence-title">
      <h2 id="onboarding-evidence-title">{{ title }}</h2>
      <p>{{ descriptions()[step() - 1] }}</p>
      @if (step() >= 2 && project(); as createdProject) {
        <dl>
          <dt>{{ projectLabel }}</dt>
          <dd>{{ createdProject.name }}</dd>
          <dt>{{ identifierLabel }}</dt>
          <dd class="identifier">{{ createdProject.id }}</dd>
        </dl>
      }
      <p class="empty">{{ empty }}</p>
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
  readonly step = input.required<StepNumber>();
  readonly project = input<ProjectDto | null>(null);
  protected readonly title = $localize`:@@onboarding.evidence.title:Created resources`;
  protected readonly projectLabel = $localize`:@@onboarding.evidence.project:Project`;
  protected readonly identifierLabel = $localize`:@@onboarding.evidence.identifier:Project ID`;
  protected readonly empty = $localize`:@@onboarding.evidence.empty:Nothing produced yet for this step.`;
  protected readonly descriptions = computed(() => [
    $localize`:@@onboarding.evidence.step-1:The created project will appear here.`,
    $localize`:@@onboarding.evidence.step-2:Connection resources will appear here.`,
    $localize`:@@onboarding.evidence.step-3:Sample-run evidence will appear here.`,
    $localize`:@@onboarding.evidence.step-4:Comparison resources will appear here.`,
    $localize`:@@onboarding.evidence.step-5:Activation resources will appear here. Collection is not enabled by viewing this step.`,
  ]);
}
