import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmSkeleton } from '@evidgate/ui/skeleton';

export type LoadingPatternLayout = 'summary' | 'form' | 'cards';

const formFields = [0, 1, 2] as const;
const summaryCards = [0, 1, 2, 3] as const;

@Component({
  selector: 'app-loading-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, HlmCard, HlmCardTitle, HlmSkeleton],
  host: { class: 'block min-w-0' },
  template: `
    @if (bare()) {
      <section
        class="state-panel state-panel--loading state-panel--bare grid min-w-0 gap-(--eg-space-5) wrap-anywhere"
        role="status"
        [attr.aria-live]="'polite'"
        aria-busy="true"
      >
        <ng-container [ngTemplateOutlet]="content" />
      </section>
    } @else {
      <section
        hlmCard
        class="state-panel state-panel--loading min-w-0 gap-(--eg-space-3) px-(--eg-space-5) py-(--eg-space-8) wrap-anywhere"
        [class]="
          layout() === 'summary'
            ? 'min-h-[calc(var(--eg-space-12)*3+var(--eg-space-8))] items-center justify-center text-center'
            : ''
        "
        role="status"
        [attr.aria-live]="'polite'"
        aria-busy="true"
      >
        <ng-container [ngTemplateOutlet]="content" />
      </section>
    }
    <ng-template #content>
      @if (layout() === 'summary') {
        <span
          hlmSkeleton
          class="loading-pattern__skeleton size-(--eg-control-touch) rounded-full"
          aria-hidden="true"
        ></span>
      }
      <h2 hlmCardTitle class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg">
        @if (label(); as suppliedLabel) {
          {{ suppliedLabel }}
        } @else {
          <span i18n="@@shared.states.loading.label">Loading evidence</span>
        }
      </h2>
      @if (layout() === 'form') {
        <div class="loading-pattern__structure grid min-w-0 gap-(--eg-space-5)" aria-hidden="true">
          @for (field of fields; track field) {
            <div class="grid min-w-0 gap-(--eg-space-3)">
              <span hlmSkeleton class="h-(--eg-leading-sm) w-1/3" aria-hidden="true"></span>
              <span hlmSkeleton class="h-(--eg-control-md) w-full" aria-hidden="true"></span>
            </div>
          }
          <span hlmSkeleton class="h-(--eg-control-md) w-1/3" aria-hidden="true"></span>
        </div>
      } @else if (layout() === 'cards') {
        <div
          class="loading-pattern__structure grid min-w-0 gap-(--eg-space-5) min-[768px]:grid-cols-2"
          aria-hidden="true"
        >
          @for (card of cards; track card) {
            <span
              hlmSkeleton
              class="h-[calc(var(--eg-space-12)*3)] min-w-0 w-full"
              aria-hidden="true"
            ></span>
          }
        </div>
      }
    </ng-template>
  `,
})
export class LoadingPatternComponent {
  readonly label = input<string | undefined>();
  /** Use form/cards to reserve the region's structure, not as a count of loaded records. */
  readonly layout = input<LoadingPatternLayout>('summary');
  /** The containing card owns its border and inset when this is true. */
  readonly bare = input(false);
  protected readonly fields = formFields;
  protected readonly cards = summaryCards;
}
