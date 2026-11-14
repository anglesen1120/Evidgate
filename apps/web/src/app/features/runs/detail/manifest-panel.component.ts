import { ChangeDetectionStrategy, Component, effect, input, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { escapeFormatChars } from '../list/ref-name-display';
import type { ManifestSummary } from './manifest-summary';
import { RUN_DETAIL_NOT_PROVIDED } from './run-value-label';

@Component({
  selector: 'app-manifest-panel',
  imports: [HlmButton, HlmCard, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './manifest-panel.component.html',
  host: {
    class:
      'block min-w-0 text-foreground wrap-anywhere [&_dl]:m-0 [&_dl]:grid [&_dl]:gap-(--eg-space-5) [&_dl]:grid-cols-1 min-[768px]:[&_dl]:grid-cols-2 [&_dl>div]:min-w-0 [&_dt]:mb-(--eg-space-2) [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:min-w-0 [&_dd]:grid [&_dd]:justify-items-start [&_dd]:gap-(--eg-space-2) [&_bdi]:max-w-full [&_bdi]:font-mono',
  },
})
export class ManifestPanelComponent {
  readonly summary = input.required<ManifestSummary>();
  protected readonly display = escapeFormatChars;
  protected readonly copyStatus = signal('');
  protected readonly copying = signal(false);
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
    copied: $localize`:@@runs.detail.copied:Manifest hash copied`,
    copyFailed: $localize`:@@runs.detail.copy-failed:Copy failed. Select and copy the full hash shown above.`,
  };
  constructor() {
    effect(() => {
      this.summary();
      this.copyStatus.set('');
    });
  }
  protected producerLabel(producer: number | string): string {
    const value = String(producer);
    return this.display(value.startsWith('shard-') ? value : `shard-${value}`);
  }
  protected async copyHash(): Promise<void> {
    const hash = this.summary().manifestHash;
    if (hash === null || this.copying()) return;
    this.copying.set(true);
    this.copyStatus.set('');
    try {
      await navigator.clipboard.writeText(hash);
      if (this.summary().manifestHash === hash) this.copyStatus.set(this.copy.copied);
    } catch {
      if (this.summary().manifestHash === hash) this.copyStatus.set(this.copy.copyFailed);
    } finally {
      this.copying.set(false);
    }
  }
}
