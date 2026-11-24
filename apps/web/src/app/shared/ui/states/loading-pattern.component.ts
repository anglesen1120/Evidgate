import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  InjectionToken,
  input,
  signal,
} from '@angular/core';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmSkeleton } from '@evidgate/ui/skeleton';

import { LiveStatusComponent } from './live-status.component';

export type LoadingPatternLayout = 'summary' | 'form' | 'cards' | 'table';

// Concurrent empty tables share a spoken label until the last matching read settles.
const pendingLabels = new InjectionToken<Map<string, number>>('pending table labels', {
  providedIn: 'root',
  factory: () => new Map<string, number>(),
});

const formFields = [0, 1, 2] as const;
const summaryCards = [0, 1, 2, 3] as const;

@Component({
  selector: 'app-loading-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, HlmCard, HlmCardTitle, HlmSkeleton, LiveStatusComponent],
  host: { class: 'relative block min-w-0' },
  styleUrl: './loading-pattern.component.css',
  template: `
    @if (layout() === 'table') {
      <app-live-status [text]="announcement()" />
      <div [attr.aria-busy]="busy()">
        @if (visible()) {
          <span
            data-table-progress
            role="progressbar"
            [attr.aria-label]="label() ?? loadingLabel"
          ></span>
          @if (!hasRows()) {
            <div class="grid gap-(--eg-space-2) p-(--eg-space-3)" aria-hidden="true">
              @for (field of fields; track field) {
                <span
                  hlmSkeleton
                  class="h-(--eg-row-default) motion-reduce:animate-none"
                  aria-hidden="true"
                ></span>
              }
            </div>
          }
        }
      </div>
    } @else if (bare()) {
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
        class="state-panel state-panel--loading min-w-0 gap-(--eg-space-3) p-(--eg-space-5) wrap-anywhere"
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
          class="loading-pattern__skeleton h-(--eg-space-3) w-1/3 max-w-40 rounded-(--eg-radius-pill)"
          aria-hidden="true"
        ></span>
      }
      <h2 hlmCardTitle class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg">
        @if (label(); as suppliedLabel) {
          {{ suppliedLabel }}
        } @else {
          <span i18n="@@shared.states.loading.label">Loading…</span>
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
  private readonly pendingLabels = inject(pendingLabels);
  readonly busy = input(false);
  readonly hasRows = input(false);
  protected readonly visible = signal(false);
  protected readonly announcement = signal('');
  protected readonly loadingLabel = $localize`:@@shared.states.loading.label:Loading…`;

  constructor() {
    effect((onCleanup) => {
      if (this.layout() !== 'table' || !this.busy()) {
        this.visible.set(false);
        return;
      }
      // A fast read still locks its trigger immediately, but never flashes progress.
      const timer = setTimeout(() => this.visible.set(true), 180);
      onCleanup(() => clearTimeout(timer));
    });
    effect((onCleanup) => {
      if (!this.visible() || this.hasRows()) {
        this.announcement.set('');
        return;
      }
      const label = this.label() ?? this.loadingLabel;
      const count = this.pendingLabels.get(label) ?? 0;
      this.pendingLabels.set(label, count + 1);
      this.announcement.set(count === 0 ? label : '');
      onCleanup(() => {
        const remaining = (this.pendingLabels.get(label) ?? 1) - 1;
        if (remaining === 0) this.pendingLabels.delete(label);
        else this.pendingLabels.set(label, remaining);
      });
    });
  }

  readonly label = input<string | undefined>();
  /** Use form/cards to reserve the region's structure, not as a count of loaded records. */
  readonly layout = input<LoadingPatternLayout>('summary');
  /** The containing card owns its border and inset when this is true. */
  readonly bare = input(false);
  protected readonly fields = formFields;
  protected readonly cards = summaryCards;
}
