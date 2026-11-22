import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard, HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { MERGE_COPY } from './studio-review-merge.copy';
import type { StudioReviewMergeViewModel } from './studio-review-merge.fixture';

@Component({
  selector: 'app-review-history',
  imports: [HlmCard, HlmCardHeader, HlmCardTitle, HlmCardContent, InlineFeedbackComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 wrap-anywhere' },
  template: `
    <section hlmCard>
      <header hlmCardHeader class="border-b border-border">
        <h2 hlmCardTitle>
          {{ view().record === 'studio' ? copy.history : copy.candidateEvidence }}
        </h2>
      </header>
      <div
        hlmCardContent
        class="grid gap-3 text-sm [&_dl]:m-0 [&_dl]:flex [&_dl]:flex-wrap [&_dl]:gap-4 [&_dt]:text-muted-foreground [&_dd]:m-0"
      >
        @if (view().record === 'studio') {
          <app-inline-feedback
            [tone]="'destructive'"
            [announcement]="'off'"
            [title]="copy.failure"
            [description]="copy.failureSummary"
          />
          <div>
            <strong>{{ copy.missingArtifact }}</strong>
            <p class="m-0 text-muted-foreground">{{ copy.missingArtifactNote }}</p>
          </div>
          <code>{{ view().references.sandbox }}</code>
          <dl>
            <div>
              <dt>{{ copy.completeness }}</dt>
              <dd>{{ copy.sandboxCompleteness }}</dd>
            </div>
            <div>
              <dt>{{ copy.sandboxOutcome }}</dt>
              <dd>
                <code>{{ view().sandboxOutcome }}</code>
              </dd>
            </div>
            <div>
              <dt>{{ copy.release }}</dt>
              <dd>{{ copy.notEvaluated }}</dd>
            </div>
          </dl>
          @if (view().state === 'merged') {
            <app-inline-feedback [announcement]="'off'" [title]="copy.mergeRecorded" />
          }
        } @else {
          <app-inline-feedback
            [tone]="'destructive'"
            [announcement]="'off'"
            [title]="copy.original"
            [description]="copy.addressFailure"
          />
          <code>{{ view().references.originalRun }}</code>
          <dl>
            <div>
              <dt>{{ copy.completeness }}</dt>
              <dd>{{ copy.incomplete }}</dd>
            </div>
            <div>
              <dt>{{ copy.ciOutcome }}</dt>
              <dd>
                <code>{{ view().references.originalOutcome }}</code>
              </dd>
            </div>
            <div>
              <dt>{{ copy.storedDecision }}</dt>
              <dd>
                <code>{{ view().references.originalDecision }}</code>
              </dd>
            </div>
          </dl>
          @if (view().validated) {
            <p class="m-0">{{ copy.shadowResult }}</p>
          }
        }
      </div>
    </section>
  `,
})
export class ReviewHistoryComponent {
  readonly view = input.required<StudioReviewMergeViewModel>();
  protected readonly copy = MERGE_COPY;
}
