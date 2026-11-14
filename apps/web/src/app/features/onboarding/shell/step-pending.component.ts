import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import type { OnboardingProgress } from '../data/onboarding-progress.service';
import type { StepNumber } from '../data/step-derivation';

@Component({
  selector: 'app-onboarding-step-pending',
  imports: [HlmAlertImports, HlmButton, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section hlmAlert role="note" class="border-dashed" data-testid="onboarding-step-pending">
      <p hlmAlertTitle>{{ hasProject() ? summaryTitle : unavailable }}</p>
      <div hlmAlertDescription class="grid min-w-0 gap-(--eg-space-4)">
        <p class="m-0">{{ description() }}</p>
        @if (progress()?.sampleRun; as run) {
          <dl
            class="m-0 grid min-w-0 gap-(--eg-space-3) [&_dd]:m-0 [&_dd]:font-mono [&_dd]:text-xs [&_dd]:[overflow-wrap:anywhere]"
          >
            <div>
              <dt>{{ runLabel }}</dt>
              <dd>{{ run.run_id }}</dd>
            </div>
            @if (run.current_evidence_revision_id; as revision) {
              <div>
                <dt>{{ evidenceLabel }}</dt>
                <dd>{{ revision }}</dd>
              </div>
            }
          </dl>
        }
        @if (runsLink(); as link) {
          <a
            hlmBtn
            [variant]="'outline'"
            class="h-auto min-h-(--eg-control-touch) max-w-full justify-self-start whitespace-normal"
            [routerLink]="link"
            data-testid="onboarding-pending-runs"
          >
            {{ runsLabel }}
          </a>
        }
      </div>
    </section>
  `,
})
export class StepPendingComponent {
  readonly progress = input<OnboardingProgress | null>(null);
  readonly orgId = input('');
  readonly step = input<StepNumber>(3);
  protected readonly hasProject = computed(() => this.progress()?.project != null);
  protected readonly runsLink = computed(() => {
    const progress = this.progress();
    return this.orgId() && progress?.project && progress.sampleRun
      ? ['/orgs', this.orgId(), 'projects', progress.project.id, 'runs']
      : null;
  });
  protected readonly description = computed(() => {
    if (!this.hasProject()) return this.retained;
    const run = this.progress()?.sampleRun;
    if (run === null || run === undefined) return this.noRun;
    if (this.step() !== 4) return this.recordedRun;
    return run.current_evidence_revision_id ? this.recordedEvidence : this.noEvidence;
  });
  protected readonly summaryTitle = $localize`:@@onboarding.pending.summary-title:Recorded setup progress`;
  protected readonly recordedRun = $localize`:@@onboarding.pending.recorded-run:The server has recorded a run for this project. This setup step does not run CI; open Runs to review the run's status, results and evidence.`;
  protected readonly recordedEvidence = $localize`:@@onboarding.pending.recorded-evidence:The server has recorded an evidence revision for this run. Setup does not display a report comparison; open Runs to inspect the run and its evidence.`;
  protected readonly noRun = $localize`:@@onboarding.pending.no-run:No run is recorded for this project yet. This setup step cannot start a sample run. Run your connected CI, then reopen setup to refresh progress.`;
  protected readonly noEvidence = $localize`:@@onboarding.pending.no-evidence:No evidence revision is recorded for this run yet. Setup does not display a report comparison; open Runs to check the run's progress.`;
  protected readonly runLabel = $localize`:@@onboarding.evidence.sample:Sample run ID`;
  protected readonly evidenceLabel = $localize`:@@onboarding.evidence.revision:Evidence revision ID`;
  protected readonly runsLabel = $localize`:@@onboarding.pending.open-runs:Open Runs`;
  protected readonly unavailable = $localize`:@@onboarding.pending.title:This step is not available yet`;
  protected readonly retained = $localize`:@@onboarding.pending.description:Your setup progress is read from the server. Viewing a step does not complete it.`;
}
