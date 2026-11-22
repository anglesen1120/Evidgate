import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import { HISTORY_COPY as c } from './investigation-history.copy';
import { checkoutHistory, compareRows, historyNotice } from './investigation-history.fixture';

@Component({
  selector: 'app-history-comparison',
  imports: [
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmSelectImports,
    HlmTableImports,
    InlineFeedbackComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './history-comparison.component.html',
})
export class HistoryComparisonComponent {
  readonly state = input.required<string>();
  readonly revision = input.required<string>();
  readonly artifactPath = input.required<readonly (string | null | undefined)[]>();
  readonly revisionChange = output<string>();
  protected readonly c = c;
  protected readonly r = checkoutHistory;
  protected readonly notice = computed(() => historyNotice(this.state()));
  protected readonly options = [
    ['ev-3', c.pair],
    ['ev-2', c.older],
    ['ev-4', c.later],
  ] as const;
  protected readonly rows = computed(() =>
    this.state() === 'changed'
      ? [
          [c.failedCount, '1', '＝ 1'],
          [c.message, c.httpError, 'HTTP 500 · req-901'],
        ]
      : compareRows,
  );
  protected choose(value: unknown): void {
    if (typeof value === 'string' && this.options.some(([id]) => id === value))
      this.revisionChange.emit(value);
  }
}
