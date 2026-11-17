import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import {
  StatusPillComponent,
  type StatusTone,
} from '../../../shared/ui/chips/status-pill.component';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
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
  ],
  host: { class: 'block min-w-0' },
  template: `
    <section hlmCard class="gap-4 p-4" aria-labelledby="policy-diff-title">
      <h3 hlmCardTitle [size]="subsectionSize" id="policy-diff-title">{{ copy.title }}</h3>
      <div class="grid min-w-0 gap-3 md:grid-cols-2">
        <div class="grid min-w-0 gap-2">
          <label hlmLabel for="policy-diff-base">{{ copy.base }}</label>
          <hlm-select
            [value]="baseId()"
            [itemToString]="revisionLabel"
            [disabled]="store.revisions().length < 2"
            (valueChange)="selectBase($event)"
          >
            <hlm-select-trigger
              [buttonId]="'policy-diff-base'"
              class="w-full min-h-[var(--eg-control-touch)] whitespace-normal"
            >
              <hlm-select-value [placeholder]="copy.choose" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal [aria-label]="copy.base">
              @for (revision of store.revisions(); track revision.id) {
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
            [disabled]="store.revisions().length === 0"
            (valueChange)="selectTarget($event)"
          >
            <hlm-select-trigger
              [buttonId]="'policy-diff-target'"
              class="w-full min-h-[var(--eg-control-touch)] whitespace-normal"
            >
              <hlm-select-value [placeholder]="copy.choose" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal [aria-label]="copy.target">
              @for (revision of store.revisions(); track revision.id) {
                <hlm-select-item [value]="revision.id"
                  >{{ copy.revision }} {{ revision.revision_no }}</hlm-select-item
                >
              }
            </hlm-select-content>
          </hlm-select>
        </div>
      </div>
      <div data-policy-diff [attr.aria-busy]="phase() === 'loading'" [attr.aria-live]="'polite'">
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
            }
            <ol class="m-0 grid list-none gap-3 p-0 empty:hidden">
              @for (row of diff().rows; track row.path) {
                <li
                  class="grid min-w-0 gap-3 rounded-md border border-solid border-border p-3"
                  data-diff-row
                  [attr.data-diff-kind]="row.kind"
                  [attr.data-diff-path]="row.path"
                >
                  <div class="flex flex-wrap items-center gap-2">
                    <app-status-pill [tone]="kindTone(row.kind)" [label]="kindLabel(row.kind)" />
                    <code class="font-mono wrap-anywhere">{{ pathLabel(row.path) }}</code>
                  </div>
                  <dl
                    class="m-0 grid min-w-0 gap-3 md:grid-cols-2 [&>div]:min-w-0 [&_dt]:text-xs [&_dt]:text-muted-foreground [&_dd]:m-0 [&_pre]:m-0 [&_pre]:whitespace-pre-wrap [&_pre]:font-mono [&_pre]:wrap-anywhere"
                  >
                    <div>
                      <dt>{{ copy.before }}</dt>
                      <dd>
                        <pre>{{ row.before ?? copy.absent }}</pre>
                      </dd>
                    </div>
                    <div>
                      <dt>{{ copy.after }}</dt>
                      <dd>
                        <pre>{{ row.after ?? copy.absent }}</pre>
                      </dd>
                    </div>
                  </dl>
                </li>
              }
            </ol>
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
    const revision = this.store.revisions().find((row) => row.id === value);
    return revision ? `${this.copy.revision} ${revision.revision_no}` : this.copy.choose;
  };
  protected readonly baseId = signal('');
  protected readonly targetId = signal('');
  protected readonly phase = signal<'empty' | 'loading' | 'ready' | 'error'>('empty');
  protected readonly error = signal<ApiError | null>(null);
  protected readonly diff = signal<PolicyDiff>({ rows: [], truncated: false });
  private generation = 0;
  protected readonly copy = {
    title: $localize`:@@settings.policy.diff-title:Compare revisions`,
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
      const rows = this.store.revisions();
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
    if (typeof value === 'string' && this.store.revisions().some((row) => row.id === value)) {
      this.baseId.set(value);
    }
  }
  protected selectTarget(value: unknown): void {
    if (typeof value === 'string' && this.store.revisions().some((row) => row.id === value)) {
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
