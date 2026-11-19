import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import type { AuthoringChangeListItem } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { PENDING_COPY } from './pending-changes.copy';
import { operationLabel, PENDING_UI } from './pending-changes.ui-copy';

@Component({
  selector: 'app-pending-confirm',
  imports: [HlmButton, HlmDialogImports, CopyableIdentifierComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [role]="'alertdialog'"
      [autoFocus]="'[data-pending-cancel]'"
      [restoreFocus]="true"
      [closeOnOutsidePointerEvents]="false"
      [aria-label]="title()"
      (stateChanged)="opened.set($event === 'open')"
      (closed)="finish()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ title() }}</h2>
          <p hlmDialogDescription>{{ description() }}</p>
        </header>
        <section class="grid min-w-0 gap-(--eg-space-3)">
          <h3 class="m-0 text-lg font-semibold">{{ idsLabel() }}</h3>
          @if (changes().length === 0) {
            @for (id of ids(); track id) {
              <app-copyable-identifier [value]="id" [label]="valueLabel()" />
            }
          }
          @for (change of changes(); track change.change_id) {
            <div class="grid min-w-0 gap-(--eg-space-2)" data-pending-consent-operation>
              <h3 class="m-0 text-lg font-semibold">{{ operationLabel(change.change_type) }}</h3>
              <app-copyable-identifier [value]="change.change_id" [label]="ui.changeId" />
              <app-copyable-identifier [value]="change.target_id" [label]="copy.targetLabel" />
              @if (targetTitles()[change.target_id]; as title) {
                <p class="m-0 whitespace-pre-wrap wrap-anywhere">
                  <bdi>{{ title }}</bdi>
                </p>
              }
              @if (change.change_type === 'delete') {
                <p class="m-0 whitespace-pre-wrap wrap-anywhere">{{ copy.deleteConsequence }}</p>
                <p class="m-0 whitespace-pre-wrap wrap-anywhere">{{ copy.impactUnavailable }}</p>
              }
            </div>
          }
        </section>
        @if (note()) {
          <dl class="min-w-0 [&_dd]:m-0">
            <dt>{{ noteLabel() }}</dt>
            <dd class="whitespace-pre-wrap wrap-anywhere">{{ note() }}</dd>
          </dl>
          @if (noteHint()) {
            <p class="text-sm text-muted-foreground">{{ noteHint() }}</p>
          }
        }
        <footer hlmDialogFooter>
          <button
            hlmBtn
            [variant]="'outline'"
            type="button"
            data-pending-cancel
            class="min-h-(--eg-control-touch)"
            (click)="opened.set(false)"
          >
            {{ copy.cancel }}
          </button>
          <button
            hlmBtn
            [variant]="destructive() ? 'destructive' : 'default'"
            type="button"
            data-pending-confirm
            class="min-h-(--eg-control-touch) whitespace-normal"
            (click)="accept()"
          >
            {{ action() }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class PendingConfirmComponent {
  readonly title = input.required<string>();
  readonly description = input.required<string>();
  readonly action = input.required<string>();
  readonly ids = input.required<readonly string[]>();
  readonly changes = input<readonly AuthoringChangeListItem[]>([]);
  readonly targetTitles = input<Readonly<Record<string, string>>>({});
  readonly note = input('');
  readonly noteLabel = input<string>(PENDING_COPY.reasonLabel);
  readonly noteHint = input<string>(PENDING_COPY.reasonHint);
  readonly idsLabel = input<string>(PENDING_UI.exactIds);
  readonly valueLabel = input<string>(PENDING_UI.changeId);
  readonly destructive = input(false);
  readonly confirmed = output<void>();
  readonly dismissed = output<void>();
  protected readonly copy = PENDING_COPY;
  protected readonly ui = PENDING_UI;
  protected readonly operationLabel = operationLabel;
  protected readonly opened = signal(true);
  private accepted = false;
  private finished = false;
  protected accept(): void {
    this.accepted = true;
    this.opened.set(false);
  }
  protected finish(): void {
    if (this.finished) return;
    this.finished = true;
    if (this.accepted) this.confirmed.emit();
    else this.dismissed.emit();
  }
}
