import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  input,
  output,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { DataTableComponent } from '../../../../shared/ui/data-table/data-table.component';
import type {
  DataTableCellContext,
  DataTableColumn,
} from '../../../../shared/ui/data-table/data-table-column';
import { NumberValueComponent } from '../../../../shared/ui/values/number-value.component';

import type { ApiError } from '../../../../core/http/api-error';
import { StatusPillComponent } from '../../../../shared/ui/chips/status-pill.component';
import { RetryAfterComponent } from '../../../../shared/ui/conflict';
import { ErrorPatternComponent } from '../../../../shared/ui/states/error-pattern.component';
import { ForbiddenPatternComponent } from '../../../../shared/ui/states/forbidden-pattern.component';
import { LoadingPatternComponent } from '../../../../shared/ui/states/loading-pattern.component';
import { NotFoundPatternComponent } from '../../../../shared/ui/states/not-found-pattern.component';
import { escapeFormatChars } from '../../list/ref-name-display';
import type {
  ReleaseComponentView,
  ReleaseDetailViewModel,
  ReleaseStatus,
} from './release-summary';

/** Release failures stay local; component bindings are narrowed to the run being viewed. */
@Component({
  selector: 'app-release-panel',
  imports: [
    HlmButton,
    HlmCard,
    HlmCardTitle,
    DataTableComponent,
    NumberValueComponent,
    StatusPillComponent,
    RetryAfterComponent,
    LoadingPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    NotFoundPatternComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './release-panel.component.html',
  host: {
    class:
      'block min-w-0 text-foreground wrap-anywhere [&_dl]:m-0 [&_dl]:grid [&_dl]:gap-(--eg-space-5) [&_dl]:grid-cols-1 min-[768px]:[&_dl]:grid-cols-2 [&_dl>div]:min-w-0 [&_dt]:mb-(--eg-space-2) [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:min-w-0 [&_dd]:grid [&_dd]:justify-items-start [&_dd]:gap-(--eg-space-2) [&_bdi]:max-w-full [&_bdi]:font-mono',
  },
})
export class ReleasePanelComponent {
  protected readonly sectionSize = 'section' as const;
  protected readonly rowId = (component: ReleaseComponentView) => component.componentKey;
  private readonly componentCell =
    viewChild.required<TemplateRef<DataTableCellContext<ReleaseComponentView>>>('componentCell');
  private readonly digestCell =
    viewChild.required<TemplateRef<DataTableCellContext<ReleaseComponentView>>>('digestCell');
  private readonly environmentCell =
    viewChild.required<TemplateRef<DataTableCellContext<ReleaseComponentView>>>('environmentCell');
  readonly releases = input<readonly { releaseId: string; name: string | null }[]>([]);
  readonly selectedReleaseId = input<string | null>(null);
  readonly selectRelease = output<string>();
  /** The run being viewed: only bindings that name it mark a component. */
  readonly runId = input.required<string>();
  readonly release = input<ReleaseDetailViewModel | null>(null);
  readonly status = input<ReleaseStatus>('idle');
  readonly error = input<ApiError | null>(null);
  readonly retryEligible = input(false);
  readonly retry = output<void>();

  protected readonly display = escapeFormatChars;
  protected readonly copyStatus = signal('');
  /** The component key whose digest is being written; only one copy runs at a time. */
  protected readonly copying = signal<string | null>(null);
  protected readonly copy = {
    title: $localize`:@@runs.detail.release.title:Release`,
    loading: $localize`:@@runs.detail.release.loading:Loading release`,
    selectLabel: $localize`:@@runs.detail.release.select-label:Releases bound to this run`,
    unbound: $localize`:@@runs.detail.release.unbound:No release bound to this run`,
    name: $localize`:@@runs.detail.release.name:Release name`,
    state: $localize`:@@runs.detail.release.state:State`,
    version: $localize`:@@runs.detail.release.version:Version`,
    boundRun: $localize`:@@runs.detail.release.bound-run:Bound run`,
    draft: $localize`:@@runs.detail.release.draft:Draft`,
    active: $localize`:@@runs.detail.release.active:Active`,
    components: $localize`:@@runs.detail.release.components:Release components`,
    component: $localize`:@@runs.detail.release.component:Component`,
    digest: $localize`:@@runs.detail.release.digest:Digest`,
    environment: $localize`:@@runs.detail.release.environment:Environment revision`,
    bound: $localize`:@@runs.detail.release.bound:Bound`,
    noComponents: $localize`:@@runs.detail.release.no-components:This release declares no components`,
    copyDigest: $localize`:@@runs.detail.release.copy-digest:Copy full digest`,
    copyFailed: $localize`:@@runs.detail.release.copy-failed:Copy failed. Select and copy the full digest shown above.`,
  };
  /** The served components, or an empty list before a release lands. */
  protected readonly components = computed<readonly ReleaseComponentView[]>(
    () => this.release()?.components ?? [],
  );
  protected readonly columns = computed<readonly DataTableColumn<ReleaseComponentView>[]>(() => [
    {
      id: 'component',
      header: this.copy.component,
      cell: this.rowId,
      role: 'identity',
      kind: 'name',
      priority: 'primary',
      overflow: 'wrap',
      cellTemplate: this.componentCell(),
    },
    {
      id: 'digest',
      header: this.copy.digest,
      cell: (component) => this.display(component.digest),
      role: 'identity',
      kind: 'identifier',
      priority: 'secondary',
      overflow: 'wrap',
      cellTemplate: this.digestCell(),
    },
    {
      id: 'environment',
      header: this.copy.environment,
      cell: (component) => this.display(component.environmentRevisionId),
      role: 'identity',
      kind: 'identifier',
      priority: 'secondary',
      overflow: 'wrap',
      cellTemplate: this.environmentCell(),
    },
  ]);
  /** The component keys `bound_runs` bound to the run being viewed, re-read per `runId`. */
  protected readonly boundKeys = computed<ReadonlySet<string>>(() => {
    const release = this.release();
    const runId = this.runId();
    if (release === null) return new Set<string>();
    return new Set(
      release.boundRuns.filter((binding) => binding.runId === runId).map((b) => b.componentKey),
    );
  });
  /** A release without any row naming this run renders the empty copy instead of its list. */
  protected readonly boundToRun = computed(() => this.boundKeys().size > 0);
  /** A refusal that names its own wait renders the countdown instead of the plain error panel. */
  protected readonly retryAfter = computed(() => {
    const failure = this.error();
    return failure?.kind === 'rate_limited' ||
      (failure?.kind === 'unavailable' && failure.retryAfterSeconds !== null)
      ? failure
      : null;
  });

  constructor() {
    effect(() => {
      this.release();
      this.copyStatus.set('');
    });
  }
  protected copyDigestLabel(rawKey: string): string {
    const componentKey = this.display(rawKey);
    return $localize`:@@runs.detail.release.copy-digest-for:Copy full digest for component ${componentKey}:key:`;
  }
  protected async copyDigest(component: ReleaseComponentView): Promise<void> {
    if (this.copying() !== null) return;
    const releaseId = this.release()?.releaseId ?? null;
    const componentKey = this.display(component.componentKey);
    this.copying.set(component.componentKey);
    this.copyStatus.set('');
    try {
      // What is pasted has to be what the table shows: a digest whose visible form differs would
      // paste invisible format characters, so it is refused instead of copied.
      if (this.display(component.digest) !== component.digest) {
        if (this.release()?.releaseId === releaseId) this.copyStatus.set(this.copy.copyFailed);
        return;
      }
      await navigator.clipboard.writeText(component.digest);
      if (this.release()?.releaseId === releaseId) {
        this.copyStatus.set(
          $localize`:@@runs.detail.release.copied-for:Digest copied for component ${componentKey}:key:`,
        );
      }
    } catch {
      if (this.release()?.releaseId === releaseId) this.copyStatus.set(this.copy.copyFailed);
    } finally {
      this.copying.set(null);
    }
  }
}
