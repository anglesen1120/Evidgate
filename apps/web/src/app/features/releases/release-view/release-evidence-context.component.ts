import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  viewChild,
  viewChildren,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';

import { SectionHeaderComponent } from '../../../shared/ui/page-header';
import { RELEASE_COPY } from './release-view-copy';
import type { ReleaseReviewState } from './release-view-state';
import type { ComponentKey, ReleaseViewModel } from './release-view.fixture';

@Component({
  selector: 'app-release-evidence-context',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmCard, HlmCardContent, HlmCardHeader, SectionHeaderComponent],
  host: {
    class:
      'grid min-w-0 items-start gap-4 wrap-anywhere xl:grid-cols-[minmax(0,5fr)_minmax(0,2fr)]',
  },
  template: `
    <section #context hlmCard tabindex="-1" aria-labelledby="release-context">
      <header hlmCardHeader>
        <app-section-header [title]="copy.context" [titleId]="'release-context'" />
      </header>
      <div hlmCardContent class="grid gap-3">
        @for (entry of view().contexts; track entry.component) {
          <article
            #fact
            tabindex="-1"
            [attr.data-component]="entry.component"
            class="min-w-0 border-0 border-s-2 border-solid border-(--eg-border-control) ps-3 first:border-(--eg-warning-fg)"
          >
            @if (state() === 'partial' && entry.component === 'worker') {
              <h3 class="eg-type-body m-0 font-semibold">{{ copy.partialWorker }}</h3>
            } @else {
              <h3 class="eg-type-body m-0 font-semibold">{{ entry.title }}</h3>
              <p class="m-0 text-sm">
                {{
                  state() === 'restricted' && entry.component !== 'worker'
                    ? copy.restricted
                    : entry.description
                }}
              </p>
            }
          </article>
        }
      </div>
    </section>
    <section hlmCard aria-labelledby="release-policy">
      <header hlmCardHeader>
        <app-section-header [title]="copy.policy" [titleId]="'release-policy'" />
      </header>
      <div hlmCardContent class="grid min-w-0 gap-2 text-sm">
        <bdi class="font-semibold">{{ view().policy }}</bdi>
        <p class="m-0">{{ copy.policyExplanation }}</p>
        <button
          hlmBtn
          [variant]="'link'"
          type="button"
          class="h-auto min-h-(--eg-control-touch) justify-self-start p-0 text-start whitespace-normal"
          (click)="openFailure.emit()"
        >
          {{ copy.separateExample }}
        </button>
      </div>
    </section>
  `,
})
export class ReleaseEvidenceContextComponent {
  readonly view = input.required<ReleaseViewModel>();
  readonly state = input.required<ReleaseReviewState>();
  readonly openFailure = output<void>();
  protected readonly copy = RELEASE_COPY;
  private readonly context = viewChild.required<ElementRef<HTMLElement>>('context');
  private readonly facts = viewChildren<ElementRef<HTMLElement>>('fact');

  focus(component?: ComponentKey): void {
    const target = this.facts().find(
      (fact) => fact.nativeElement.dataset['component'] === component,
    );
    (target ?? this.context()).nativeElement.focus();
  }
}
