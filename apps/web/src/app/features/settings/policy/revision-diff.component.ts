import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  output,
  signal,
  untracked,
} from '@angular/core';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { clipDiffText, diffPolicyBodies, type PolicyDiff } from './policy-diff';
import { PolicyStore } from './policy.store';

@Component({
  selector: 'app-revision-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './policy.page.css',
  template: `
    <section aria-labelledby="policy-diff-title">
      <h3 id="policy-diff-title">{{ copy.title }}</h3>
      <div class="comparison-pickers">
        <div class="field">
          <label for="policy-diff-base">{{ copy.base }}</label>
          <!--
            The selection is carried by each option's selected state, never by a value binding on
            the select: the select exists (with only its placeholder) before the revisions arrive,
            so a value written then is discarded and the browser would keep showing the placeholder.
          -->
          <select
            id="policy-diff-base"
            [disabled]="store.revisions().length < 2"
            (change)="selectBase($event)"
          >
            <option value="">{{ copy.choose }}</option>
            @for (revision of store.revisions(); track revision.id) {
              <option [value]="revision.id" [selected]="revision.id === baseId()">
                {{ copy.revision }} {{ revision.revision_no }}
              </option>
            }
          </select>
        </div>
        <div class="field">
          <label for="policy-diff-target">{{ copy.target }}</label>
          <select
            id="policy-diff-target"
            [disabled]="store.revisions().length === 0"
            (change)="selectTarget($event)"
          >
            <option value="">{{ copy.choose }}</option>
            @for (revision of store.revisions(); track revision.id) {
              <option [value]="revision.id" [selected]="revision.id === targetId()">
                {{ copy.revision }} {{ revision.revision_no }}
              </option>
            }
          </select>
        </div>
      </div>
      <div data-policy-diff [attr.aria-busy]="phase() === 'loading'" [attr.aria-live]="'polite'">
        @switch (phase()) {
          @case ('loading') {
            <p class="state" role="status">{{ copy.loading }}</p>
          }
          @case ('error') {
            <div class="failure" role="alert">
              <p>{{ error()?.message ?? copy.error }}</p>
              @if (error(); as failure) {
                <code>{{ failure.code }}</code>
              }
            </div>
            <button type="button" (click)="load()">{{ copy.retry }}</button>
          }
          @case ('empty') {
            <p class="state">{{ copy.empty }}</p>
          }
          @case ('ready') {
            @if (diff().rows.length === 0) {
              <p class="state">{{ copy.same }}</p>
            }
            <ol class="diff-rows">
              @for (row of diff().rows; track $index) {
                <li data-diff-row [attr.data-diff-kind]="row.kind" [attr.data-diff-path]="row.path">
                  <div class="revision-heading">
                    <strong>{{ kindLabel(row.kind) }}</strong
                    ><code>{{ pathLabel(row.path) }}</code>
                  </div>
                  <dl class="comparison-values">
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
              <p class="notice" data-policy-diff-truncated>{{ copy.truncated }}</p>
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
      untracked(() => {
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
  /**
   * A row's JSON pointer as the list shows it: clipped to the same budget its two values are (D3).
   * A pointer can carry a whole key from the body, so an unclipped one would print a body fragment
   * as a heading. The raw pointer stays on the row's `data-diff-path` attribute, where a test reads
   * it without going through the rendering budget.
   */
  protected pathLabel(path: string): string {
    return clipDiffText(path);
  }
  protected selectBase(event: Event): void {
    const select = event.target;
    if (
      select instanceof HTMLSelectElement &&
      this.store.revisions().some((row) => row.id === select.value)
    )
      this.baseId.set(select.value);
  }
  protected selectTarget(event: Event): void {
    const select = event.target;
    if (
      select instanceof HTMLSelectElement &&
      this.store.revisions().some((row) => row.id === select.value)
    )
      this.targetId.set(select.value);
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
