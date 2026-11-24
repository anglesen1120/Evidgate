import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  untracked,
} from '@angular/core';
import type { PolicyRevisionSummaryDto } from '@evidgate/sdk';
import { HlmAlertImports } from '@evidgate/ui/alert';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../shared/ui/chips/status-pill.component';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { clipDiffText, diffPolicyBodies, type PolicyDiff } from './policy-diff';
import { PolicyStore } from './policy.store';

@Component({
  selector: 'app-revision-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmAlertImports,
    StatusPillComponent,
    HlmButton,
    HlmCardImports,
    HlmLabel,
    HlmSelectImports,
    HlmTableImports,
  ],
  host: { class: 'block min-w-0' },
  template: `
    <section hlmCard class="min-w-0 gap-0 rounded-lg py-0" aria-labelledby="policy-diff-title">
      <header hlmCardHeader class="p-(--eg-space-5)">
        <h3 hlmCardTitle [size]="subsectionSize" id="policy-diff-title">{{ copy.title }}</h3>
      </header>
      <div
        class="grid min-w-0 gap-(--eg-space-3) px-(--eg-space-5) pb-(--eg-space-5) sm:grid-cols-2"
      >
        <div class="grid min-w-0 gap-2">
          <label hlmLabel for="policy-diff-base">{{ copy.base }}</label>
          <hlm-select
            [value]="baseId()"
            [itemToString]="revisionLabel"
            [disabled]="choices().length < 2"
            (valueChange)="selectBase($event)"
          >
            <hlm-select-trigger
              [buttonId]="'policy-diff-base'"
              class="w-full min-h-[var(--eg-control-touch)] whitespace-normal"
            >
              <hlm-select-value [placeholder]="copy.choose" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal [aria-label]="copy.base">
              @for (revision of choices(); track revision.id) {
                <hlm-select-item [value]="revision.id"
                  >{{ copy.revision }} {{ revision.revision_no }}</hlm-select-item
                >
              }
            </hlm-select-content>
          </hlm-select>
        </div>
        <div class="grid min-w-0 gap-2">
          <label hlmLabel for="policy-diff-target">{{ copy.target }}</label>
          <hlm-select
            [value]="targetId()"
            [itemToString]="revisionLabel"
            [disabled]="choices().length === 0"
            (valueChange)="selectTarget($event)"
          >
            <hlm-select-trigger
              [buttonId]="'policy-diff-target'"
              class="w-full min-h-[var(--eg-control-touch)] whitespace-normal"
            >
              <hlm-select-value [placeholder]="copy.choose" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal [aria-label]="copy.target">
              @for (revision of choices(); track revision.id) {
                <hlm-select-item [value]="revision.id"
                  >{{ copy.revision }} {{ revision.revision_no }}</hlm-select-item
                >
              }
            </hlm-select-content>
          </hlm-select>
        </div>
      </div>
      <div
        class="min-w-0 border-0 border-t border-solid border-border"
        data-policy-diff
        [attr.aria-busy]="phase() === 'loading'"
        [attr.aria-live]="'polite'"
      >
        @switch (phase()) {
          @case ('loading') {
            <p hlmAlert class="m-0 text-muted-foreground" role="status">{{ copy.loading }}</p>
          }
          @case ('error') {
            <div hlmAlert [variant]="'destructive'" class="mb-3 wrap-anywhere" role="alert">
              <p>{{ error()?.message ?? copy.error }}</p>
              @if (error(); as failure) {
                <code>{{ failure.code }}</code>
              }
            </div>
            <button
              hlmBtn
              [variant]="'outline'"
              class="min-h-[var(--eg-control-touch)] whitespace-normal"
              type="button"
              (click)="load()"
            >
              {{ copy.retry }}
            </button>
          }
          @case ('empty') {
            <p hlmAlert class="m-0 text-muted-foreground">{{ copy.empty }}</p>
          }
          @case ('ready') {
            @if (diff().rows.length === 0) {
              <p hlmAlert class="m-0 text-muted-foreground">{{ copy.same }}</p>
            } @else {
              <div hlmTableContainer>
                <table
                  hlmTable
                  class="w-full table-fixed border-collapse [&_th]:whitespace-normal [&_pre]:m-0 [&_pre]:whitespace-pre-wrap [&_pre]:font-mono [&_pre]:wrap-anywhere"
                  [attr.aria-labelledby]="'policy-diff-title'"
                >
                  <thead hlmTHead class="bg-(--eg-surface-raised) text-xs">
                    <tr hlmTr>
                      <th hlmTh scope="col" class="w-2/5">{{ copy.field }}</th>
                      <th hlmTh scope="col">{{ copy.before }}</th>
                      <th hlmTh scope="col">{{ copy.after }}</th>
                    </tr>
                  </thead>
                  <tbody hlmTBody>
                    @for (row of diff().rows; track row) {
                      <tr
                        hlmTr
                        class="border-0 border-t border-solid border-border"
                        data-diff-row
                        [attr.data-diff-kind]="row.kind"
                        [attr.data-diff-path]="row.path"
                      >
                        <th hlmTh scope="row" class="py-(--eg-space-3) align-top">
                          <code class="block font-mono wrap-anywhere">{{
                            pathLabel(row.path)
                          }}</code>
                        </th>
                        <td hlmTd class="align-top">
                          <pre>{{ row.before ?? copy.absent }}</pre>
                        </td>
                        <td hlmTd class="align-top">
                          <div class="flex min-w-0 flex-wrap items-start gap-(--eg-space-2)">
                            <pre class="min-w-0 font-semibold text-(--eg-accent)">{{
                              row.after ?? copy.absent
                            }}</pre>
                            <app-status-pill
                              [tone]="kindTone(row.kind)"
                              [label]="kindLabel(row.kind)"
                            />
                          </div>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @if (diff().truncated) {
              <p hlmAlert class="mt-3 bg-muted text-muted-foreground" data-policy-diff-truncated>
                {{ copy.truncated }}
              </p>
            }
          }
        }
      </div>
    </section>
  `,
})
export class RevisionDiffComponent {
  protected readonly store = inject(PolicyStore);
  readonly targetChanged = output<string | null>();
  readonly selectedRevisionId = input<string | null>(null);
  protected readonly subsectionSize = 'subsection' as const;
  protected readonly revisionLabel = (value: unknown): string => {
    const revision = this.choices().find((row) => row.id === value);
    return revision ? `${this.copy.revision} ${revision.revision_no}` : this.copy.choose;
  };
  protected readonly baseId = signal('');
  protected readonly targetId = signal('');
  protected readonly choices = linkedSignal({
    source: () => ({
      rows: this.store.revisions(),
      opened: this.store.openedRevision(),
      project: this.store.projectId(),
      base: this.baseId(),
      target: this.targetId(),
    }),
    computation: (source, previous): readonly PolicyRevisionSummaryDto[] => {
      const rows = source.opened && !source.rows.some((row) => row.id === source.opened?.id)
        ? [...source.rows, source.opened]
        : source.rows;
      const pinned = previous?.source.project === source.project
        ? previous?.value.filter((row) => row.id === source.base || row.id === source.target) ?? []
        : [];
      return [
        ...rows,
        ...pinned.filter((row) => !rows.some((current) => current.id === row.id)),
      ];
    },
  });
  protected readonly phase = signal<'empty' | 'loading' | 'ready' | 'error'>('empty');
  protected readonly error = signal<ApiError | null>(null);
  protected readonly diff = signal<PolicyDiff>({ rows: [], truncated: false });
  private generation = 0;
  protected readonly copy = {
    title: $localize`:@@settings.policy.diff-title:Compare revisions`,
    field: $localize`:@@settings.policy.diff-field:Field`,
    base: $localize`:@@settings.policy.diff-base:Base revision`,
    target: $localize`:@@settings.policy.diff-target:Target revision`,
    choose: $localize`:@@settings.policy.diff-choose:Choose a revision`,
    revision: $localize`:@@settings.policy.revision:Revision`,
    loading: $localize`:@@settings.policy.diff-loading:Loading revision bodies…`,
    error: $localize`:@@settings.policy.diff-error:Revision bodies could not be loaded.`,
    retry: $localize`:@@settings.policy.diff-retry:Reload comparison`,
    empty: $localize`:@@settings.policy.diff-empty:Select two revisions to compare their fields.`,
    same: $localize`:@@settings.policy.diff-same:No field differences.`,
    before: $localize`:@@settings.policy.diff-before:Before`,
    after: $localize`:@@settings.policy.diff-after:After`,
    absent: $localize`:@@settings.policy.diff-absent:Not present`,
    added: $localize`:@@settings.policy.diff-added:Added`,
    removed: $localize`:@@settings.policy.diff-removed:Removed`,
    changed: $localize`:@@settings.policy.diff-changed:Changed`,
    truncated: $localize`:@@settings.policy.diff-truncated:This comparison is truncated. Not all field differences are shown.`,
  };
  constructor() {
    effect(() => {
      const rows = this.choices();
      const head = this.store.headRevisionId();
      const selected = this.selectedRevisionId();
      untracked(() => {
        if (selected && rows.some((row) => row.id === selected)) this.targetId.set(selected);
        if (!rows.some((row) => row.id === this.targetId())) {
          const target = rows.find((row) => row.id === head) ?? rows[rows.length - 1];
          this.targetId.set(target?.id ?? '');
        }
        if (!rows.some((row) => row.id === this.baseId())) {
          const target = rows.find((row) => row.id === this.targetId());
          const previous = target
            ? rows.filter((row) => row.revision_no < target.revision_no).at(-1)
            : undefined;
          this.baseId.set(previous?.id ?? '');
        }
      });
    });
    effect(() => {
      const base = this.baseId();
      const target = this.targetId();
      untracked(() => {
        this.targetChanged.emit(target || null);
        void this.loadPair(base, target);
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.generation++;
    });
  }
  protected kindLabel(kind: 'added' | 'removed' | 'changed'): string {
    return this.copy[kind];
  }
  protected kindTone(kind: 'added' | 'removed' | 'changed'): StatusTone {
    return kind === 'added' ? 'info' : kind === 'removed' ? 'neutral' : 'warning';
  }
  // Pointers can contain a whole body key; keep the visible label within the value budget.
  protected pathLabel(path: string): string {
    return clipDiffText(path);
  }
  protected selectBase(value: unknown): void {
    if (typeof value === 'string' && this.choices().some((row) => row.id === value)) {
      this.baseId.set(value);
    }
  }
  protected selectTarget(value: unknown): void {
    if (typeof value === 'string' && this.choices().some((row) => row.id === value)) {
      this.targetId.set(value);
    }
  }
  protected load(): void {
    void this.loadPair(this.baseId(), this.targetId());
  }
  private async loadPair(base: string, target: string): Promise<void> {
    const generation = ++this.generation;
    this.error.set(null);
    this.diff.set({ rows: [], truncated: false });
    if (!base || !target) {
      this.phase.set('empty');
      return;
    }
    this.phase.set('loading');
    try {
      const [before, after] = await Promise.all([
        this.store.loadBody(base),
        this.store.loadBody(target),
      ]);
      if (generation !== this.generation) return;
      this.diff.set(diffPolicyBodies(before.body, after.body));
      this.phase.set('ready');
    } catch (error: unknown) {
      if (generation !== this.generation) return;
      this.error.set(isApiRequestError(error) ? error.error : null);
      this.phase.set('error');
    }
  }
}
