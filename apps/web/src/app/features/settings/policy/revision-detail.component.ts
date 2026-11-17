import { JsonPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import type { PolicyRevisionDto } from '@evidgate/sdk';
import { HlmCardImports } from '@evidgate/ui/card';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type { DataTableColumn } from '../../../shared/ui/data-table/data-table-column';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { PolicyRevisionStateComponent } from './policy-revision-state.component';
import { PolicyStore } from './policy.store';

interface ReferenceRow {
  readonly index: number;
  readonly value: string;
}

@Component({
  selector: 'app-policy-revision-detail',
  imports: [
    JsonPipe,
    HlmCardImports,
    AbsoluteTimeComponent,
    DataTableComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    CopyableIdentifierComponent,
    NumberValueComponent,
    PolicyRevisionStateComponent,
  ],
  templateUrl: './revision-detail.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-7)', '[attr.aria-busy]': 'phase() === "loading"' },
})
export class RevisionDetailComponent {
  private readonly store = inject(PolicyStore);

  readonly revisionId = input<string | null>(null);
  protected readonly revision = signal<PolicyRevisionDto | null>(null);
  protected readonly phase = signal<'empty' | 'loading' | 'ready' | 'error'>('empty');
  protected readonly error = signal<ApiError | null>(null);
  protected readonly subsectionSize = 'subsection' as const;
  protected readonly loadingLayout = 'form' as const;
  protected readonly destructive = 'destructive' as const;
  protected readonly referenceId = (row: ReferenceRow): number => row.index;
  private generation = 0;
  protected readonly copy = {
    title: $localize`:@@settings.policy.selected-title:Selected revision`,
    values: $localize`:@@settings.policy.values-title:Policy values`,
    id: $localize`:@@settings.policy.revision-id:Revision ID`,
    hash: $localize`:@@settings.policy.hash:Hash`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    state: $localize`:@@settings.policy.state:Revision state`,
    rawState: $localize`:@@settings.policy.raw-state:Recorded state`,
    created: $localize`:@@settings.policy.created:Created`,
    createdBy: $localize`:@@settings.policy.created-by:Created by (identifier)`,
    loading: $localize`:@@settings.policy.detail-loading:Loading selected revision…`,
    failure: $localize`:@@settings.policy.detail-failed:The selected revision could not be loaded.`,
    retry: $localize`:@@settings.policy.detail-retry:Reload selected revision`,
    empty: $localize`:@@settings.policy.detail-empty:Select a revision to inspect its complete policy.`,
    notSupplied: $localize`:@@settings.policy.not-supplied:Not supplied`,
    noValues: $localize`:@@settings.policy.no-values:No policy fields were supplied.`,
    critical: $localize`:@@settings.policy.critical-cases:Critical cases`,
    quarantine: $localize`:@@settings.policy.quarantine-references:Quarantine references`,
    references: $localize`:@@settings.policy.reference-id:Reference identifier`,
    noReferences: $localize`:@@settings.policy.no-references:No references in this revision.`,
    referenceNote: $localize`:@@settings.policy.reference-note:References belong to the selected revision. Names, quarantine lifecycle, reasons and expiry are not supplied by this policy body.`,
    immutable: $localize`:@@settings.policy.immutable-note:Publishing appends a revision. It does not rewrite prior evaluations or release decisions, or establish an activation mode.`,
  };
  private readonly labels: Readonly<Record<string, string>> = {
    required_suites: $localize`:@@settings.policy.field.required-suites:Required suites`,
    required_cases: $localize`:@@settings.policy.field.required-cases:Required cases`,
    allowed_skips: $localize`:@@settings.policy.field.allowed-skips:Allowed skips`,
    flaky_budget: $localize`:@@settings.policy.field.flaky-budget:Flaky budget`,
    required_artifacts: $localize`:@@settings.policy.field.required-artifacts:Required artifacts`,
    freshness_minutes: $localize`:@@settings.policy.field.freshness:Freshness limit (minutes)`,
    collection_timeout_minutes: $localize`:@@settings.policy.field.collection-timeout:Collection timeout (minutes)`,
    late_window_minutes: $localize`:@@settings.policy.field.late-window:Late evidence window (minutes)`,
    gating_refs: $localize`:@@settings.policy.field.gating-refs:Gating references`,
  };
  private readonly entries = computed<readonly [string, unknown][]>(() => {
    const body: unknown = this.revision()?.body;
    return body !== null && typeof body === 'object' && !Array.isArray(body)
      ? Object.entries(body)
      : [];
  });
  protected readonly fields = computed(() =>
    this.entries()
      .filter(([key]) => key !== 'critical_cases' && key !== 'quarantine_ids')
      .map(([key, value]) => ({ key, label: this.labels[key] ?? key, value })),
  );
  protected readonly groups = computed(() =>
    [
      { key: 'critical_cases', label: this.copy.critical },
      { key: 'quarantine_ids', label: this.copy.quarantine },
    ].map((group) => {
      const value = this.entries().find(([key]) => key === group.key)?.[1];
      const values: readonly unknown[] | null = Array.isArray(value) ? value : null;
      const rows: readonly ReferenceRow[] | null =
        values?.map((entry, index) => ({
          index,
          value: typeof entry === 'string' ? entry : (JSON.stringify(entry) ?? String(entry)),
        })) ?? null;
      return { ...group, value, rows };
    }),
  );
  protected readonly referenceColumns: readonly DataTableColumn<ReferenceRow>[] = [
    {
      id: 'reference',
      header: this.copy.references,
      role: 'identity',
      kind: 'identifier',
      priority: 'primary',
      overflow: 'wrap',
      cell: (row) => row.value,
    },
  ];

  constructor() {
    effect(() => {
      const id = this.revisionId();
      const project = this.store.projectId();
      untracked(() => void this.load(id, project));
    });
    inject(DestroyRef).onDestroy(() => this.generation++);
  }

  protected retry(): void {
    void this.load(this.revisionId(), this.store.projectId());
  }

  private async load(id: string | null, project: string | null): Promise<void> {
    const generation = ++this.generation;
    this.revision.set(null);
    this.error.set(null);
    if (!id || !project) {
      this.phase.set('empty');
      return;
    }
    this.phase.set('loading');
    try {
      const revision = await this.store.loadBody(id);
      if (generation !== this.generation || project !== this.store.projectId()) return;
      this.revision.set(revision);
      this.phase.set('ready');
    } catch (error: unknown) {
      if (generation !== this.generation || project !== this.store.projectId()) return;
      this.error.set(isApiRequestError(error) ? error.error : null);
      this.phase.set('error');
    }
  }
}
