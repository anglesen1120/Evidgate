import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CopyValueButtonComponent } from '../../../shared/ui/values/copy-value-button.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import type { ManifestSummary } from './manifest-summary';
import { RUN_DETAIL_NOT_PROVIDED } from './run-value-label';

@Component({
  selector: 'app-manifest-panel',
  imports: [CopyValueButtonComponent, NumberValueComponent, HlmCard, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './manifest-panel.component.html',
  host: {
    class:
      'block min-w-0 text-foreground wrap-anywhere [&_dl]:m-0 [&_dl]:grid [&_dl]:gap-(--eg-space-5) [&_dl]:grid-cols-1 min-[768px]:[&_dl]:grid-cols-2 [&_dl>div]:min-w-0 [&_dt]:mb-(--eg-space-2) [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:min-w-0 [&_dd]:grid [&_dd]:justify-items-start [&_dd]:gap-(--eg-space-2) [&_bdi]:max-w-full [&_bdi]:font-mono',
  },
})
export class ManifestPanelComponent {
  readonly summary = input.required<ManifestSummary>();
  protected readonly sectionSize = 'section' as const;
  protected readonly display = escapeFormatChars;
  protected readonly copy = {
    title: $localize`:@@runs.detail.manifest:Manifest summary`,
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    hash: $localize`:@@runs.detail.manifest-hash:Manifest hash`,
    suite: $localize`:@@runs.detail.suite:Suite revision`,
    policy: $localize`:@@runs.detail.policy:Policy revision`,
    shards: $localize`:@@runs.detail.shards:Shard count`,
    producers: $localize`:@@runs.detail.producers:Declared producers`,
    noProducers: $localize`:@@runs.detail.no-producers:No producers declared`,
    copyHash: $localize`:@@runs.detail.copy-hash:Copy full manifest hash`,
  };
  protected producerLabel(producer: number | string): string {
    const value = String(producer);
    return this.display(value.startsWith('shard-') ? value : `shard-${value}`);
  }
}
