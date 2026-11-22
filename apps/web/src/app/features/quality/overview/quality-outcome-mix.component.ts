import { DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import type { QualityDayViewModel } from './quality-overview.fixture';

@Component({
  selector: 'app-quality-outcome-mix',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, PercentPipe, HlmCardImports, HlmIcon],
  host: { class: 'block min-w-0' },
  template: `
    <section hlmCard [size]="'sm'" aria-labelledby="quality-mix-title">
      <header hlmCardHeader class="border-b border-border">
        <h2 hlmCardTitle [size]="'subsection'" id="quality-mix-title">{{ copy.title }}</h2>
        <p hlmCardDescription>{{ copy.scope }}</p>
      </header>
      @let mix = breakdown();
      <div hlmCardContent class="grid min-w-0 gap-4">
        @if (mix.total > 0) {
          <div class="relative mx-auto grid size-32 place-items-center">
            <svg
              aria-hidden="true"
              focusable="false"
              viewBox="0 0 100 100"
              class="absolute inset-0 size-full -rotate-90"
            >
              @for (slice of mix.slices; track slice.key) {
                @if (slice.count) {
                  <circle
                    cx="50"
                    cy="50"
                    r="42"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="12"
                    [attr.pathLength]="mix.total"
                    [attr.stroke-dasharray]="slice.count + ' ' + (mix.total - slice.count)"
                    [attr.stroke-dashoffset]="-slice.start"
                    [class]="slice.color"
                  />
                }
              }
            </svg>
            <strong class="font-(family-name:--eg-font-display) text-2xl tabular-nums">{{
              mix.total | number
            }}</strong>
          </div>
          <dl class="m-0 grid gap-2">
            @for (slice of mix.slices; track slice.key) {
              <div class="flex items-center justify-between gap-2">
                <dt class="flex min-w-0 items-center gap-2">
                  <hlm-icon [name]="slice.icon" [class]="slice.color" class="size-4" />{{
                    slice.label
                  }}
                </dt>
                <dd class="m-0 whitespace-nowrap text-end tabular-nums">
                  {{ slice.count | number }} · {{ slice.count / mix.total | percent: '1.0-1' }}
                </dd>
              </div>
            }
          </dl>
        } @else {
          <p class="m-0 text-sm text-muted-foreground">
            {{ mix.observed ? copy.zero : copy.noData }}
          </p>
        }
      </div>
    </section>
  `,
})
export class QualityOutcomeMixComponent {
  readonly days = input.required<readonly QualityDayViewModel[]>();
  protected readonly copy = {
    title: $localize`:@@review.quality.outcome-mix:Outcome mix`,
    scope: $localize`:@@review.quality.outcome-mix-scope:Complete runs on reported days only.`,
    zero: $localize`:@@review.quality.zero-complete:No complete runs on reported days.`,
    noData: $localize`:@@review.quality.no-data:No data`,
  };
  protected readonly breakdown = computed(() => {
    let passed = 0,
      failed = 0,
      other = 0,
      observed = false;
    for (const { observation } of this.days()) {
      if (!observation) continue;
      observed = true;
      passed += observation.passed;
      failed += observation.failed;
      other += observation.other;
    }
    const slices = [
      {
        key: 'passed',
        label: $localize`:@@review.quality.passed:Passed`,
        count: passed,
        start: 0,
        color: 'text-(--eg-accent)',
        icon: 'lucideCheck',
      },
      {
        key: 'failed',
        label: $localize`:@@review.quality.failed:Failed`,
        count: failed,
        start: passed,
        color: 'text-(--eg-danger-fg)',
        icon: 'lucideOctagonX',
      },
      {
        key: 'other',
        label: $localize`:@@review.quality.other:Other`,
        count: other,
        start: passed + failed,
        color: 'text-(--eg-neutral-fg)',
        icon: 'lucideCircle',
      },
    ] as const;
    return { total: passed + failed + other, observed, slices };
  });
}
