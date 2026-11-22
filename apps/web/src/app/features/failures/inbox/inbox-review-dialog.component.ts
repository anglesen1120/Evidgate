import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { INBOX_COPY } from './failure-inbox.copy';
import { LABELS, TRIAGE_PRECONDITION, type IncidentId } from './failure-inbox.fixture';
import { FailureInboxStore } from './failure-inbox.store';
import { InboxChoiceComponent } from './inbox-choice.component';

@Component({
  selector: 'app-inbox-review-dialog',
  imports: [HlmButton, HlmDialogImports, InboxChoiceComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="records().length ? 'open' : 'closed'"
      [aria-label]="c.reviewTitle"
      (closed)="close()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[90dvh] min-w-0 overflow-y-auto wrap-anywhere"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ c.reviewTitle }}</h2>
          <p hlmDialogDescription>{{ c.exact }}</p>
        </header>
        <ul class="grid gap-2 ps-5">
          @for (row of records(); track row.id) {
            <li>
              <strong>{{ row.id }}</strong> ·
              <code>{{ row.id === 'inc-91' ? checkoutPrecondition() : originalPrecondition }}</code>
              · {{ row.title }}
            </li>
          }
        </ul>
        <app-inbox-choice
          [fieldId]="'inbox-operation'"
          [label]="c.change"
          [options]="operations"
          [value]="operation()"
          (changed)="changeOperation($event)"
        />
        <app-inbox-choice
          [fieldId]="'inbox-target'"
          [label]="operation() === 'assignOwner' ? c.newOwner : c.newLabel"
          [options]="targets()"
          [value]="target()"
          (changed)="chooseTarget($event)"
        />
        <p class="text-sm text-muted-foreground">{{ c.previewOnly }}</p>
        <footer hlmDialogFooter>
          <button hlmBtn type="button" [variant]="'outline'" hlmDialogClose>{{ c.cancel }}</button>
          <button hlmBtn type="button" [disabled]="!records().length" (click)="confirm()">
            {{ c.confirm }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class InboxReviewDialogComponent {
  protected readonly store = inject(FailureInboxStore);
  private readonly viewport = inject(NarrowMutationPolicy);
  protected readonly c = INBOX_COPY;
  protected readonly operations = ['assignOwner', 'setLabel'] as const;
  protected readonly operation = signal<'assignOwner' | 'setLabel'>('assignOwner');
  protected readonly targets = computed(() =>
    this.operation() === 'assignOwner' ? (['minh', 'linh'] as const) : LABELS,
  );
  protected readonly target = linkedSignal<keyof typeof INBOX_COPY>(() => this.targets()[0]);
  private readonly ids = linkedSignal<readonly IncidentId[]>(() => {
    this.store.query();
    this.store.scope();
    this.viewport.canMutate();
    return [];
  });
  protected readonly originalPrecondition = TRIAGE_PRECONDITION.original;
  protected readonly checkoutPrecondition = computed(() =>
    this.store.state() === 'latest' || this.store.state() === 'saved'
      ? TRIAGE_PRECONDITION.updated
      : TRIAGE_PRECONDITION.original,
  );
  protected readonly records = computed(() =>
    this.store.visible().filter((row) => this.ids().includes(row.id)),
  );

  open(ids: readonly IncidentId[]): void {
    if (
      !this.viewport.allowsMutation() ||
      !this.store.showQueue() ||
      this.store.state() === 'submitting'
    )
      return;
    this.operation.set('assignOwner');
    this.target.set('minh');
    this.ids.set(ids);
  }
  protected close(): void {
    this.ids.set([]);
  }
  protected changeOperation(value: keyof typeof INBOX_COPY): void {
    if (value === 'setLabel' || value === 'assignOwner') this.operation.set(value);
  }
  protected chooseTarget(value: keyof typeof INBOX_COPY): void {
    if (this.targets().some((key) => key === value)) this.target.set(value);
  }
  protected confirm(): void {
    const rows = this.records();
    if (
      this.viewport.allowsMutation() &&
      this.store.showQueue() &&
      this.store.state() !== 'submitting' &&
      rows.length
    ) {
      this.store.confirmation.set(
        `${this.c.confirmedPreview} ${rows.map((row) => row.id).join(', ')} → ${this.c[this.target()]}`,
      );
    }
    this.close();
  }
}
