import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import {
  EmptyPatternComponent,
  ForbiddenPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { REPLACEMENT, REVIEWED } from './repair-review.fixture';
import { SHADOW_COPY } from './shadow.copy';
import type { ReviewState } from './repair-review-context';

@Component({
  selector: 'app-repair-review-notice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmButton,
    EmptyPatternComponent,
    ForbiddenPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
  ],
  template: `
    @switch (state()) {
      @case ('loading') {
        <app-loading-pattern [label]="copy.loading" [layout]="'cards'" />
      }
      @case ('empty') {
        <app-empty-pattern
          [icon]="'navQueue'"
          [title]="copy.empty"
          [description]="copy.emptyNote"
        />
      }
      @case ('403') {
        <app-forbidden-pattern />
      }
      @case ('404') {
        <app-not-found-pattern />
      }
      @case ('401') {
        <app-inline-feedback [title]="copy.session" [tone]="'destructive'" />
      }
      @case ('429') {
        <app-inline-feedback [title]="copy.limited" />
      }
      @case ('error') {
        <app-inline-feedback [title]="copy.error" [tone]="'destructive'"
          ><a
            feedbackAction
            hlmBtn
            [variant]="'outline'"
            [routerLink]="[]"
            [queryParamsHandling]="'merge'"
            [queryParams]="{ state: null }"
            >{{ copy.retry }}</a
          ></app-inline-feedback
        >
      }
      @case ('filtered') {
        <app-inline-feedback [title]="copy.filtered"
          ><a
            feedbackAction
            hlmBtn
            [variant]="'outline'"
            [routerLink]="[]"
            [queryParamsHandling]="'merge'"
            [queryParams]="{ state: null }"
            >{{ copy.clear }}</a
          ></app-inline-feedback
        >
      }
      @case ('412') {
        <app-inline-feedback [title]="copy.revisionChanged" [tone]="'destructive'"
          ><code>{{ beforeHash }} → {{ afterHash }}</code
          ><a
            feedbackAction
            hlmBtn
            [variant]="'outline'"
            [routerLink]="[]"
            [queryParamsHandling]="'merge'"
            [queryParams]="{ state: 'reloaded' }"
            >{{ copy.reload }}</a
          ></app-inline-feedback
        >
      }
      @case ('reloaded') {
        <app-inline-feedback [title]="copy.reloaded" />
      }
      @case ('unavailable') {
        <app-inline-feedback [title]="copy.unavailable" [tone]="'destructive'" />
      }
    }
  `,
})
export class RepairReviewNoticeComponent {
  readonly state = input.required<ReviewState>();
  protected readonly copy = SHADOW_COPY;
  protected readonly beforeHash = REVIEWED.hash;
  protected readonly afterHash = REPLACEMENT.hash;
}
