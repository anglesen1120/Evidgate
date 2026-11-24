import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import type { NodeDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { RowActionComponent } from '../../shared/ui/data-table/row-action.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { NumberValueComponent } from '../../shared/ui/values/number-value.component';
import { AUTHORING_TREE_COPY } from './authoring-tree.copy';

@Component({
  selector: 'app-authoring-node-details',
  imports: [
    HlmButton,
    HlmDialogImports,
    RowActionComponent,
    CopyableIdentifierComponent,
    NumberValueComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-flex shrink-0 self-center' },
  template: `
    <button
      #opener
      appRowAction
      type="button"
      [icon]="'lucideInfo'"
      [label]="label()"
      [attr.aria-label]="label()"
      [attr.aria-haspopup]="'dialog'"
      [attr.data-node-details]="node().node_id"
      (click)="open.set(true)"
    ></button>
    @if (open()) {
      <hlm-dialog
        [state]="'open'"
        [aria-modal]="true"
        [autoFocus]="'first-heading'"
        [restoreFocus]="opener.nativeElement"
        (closed)="open.set(false)"
      >
        <hlm-dialog-content *hlmDialogPortal [size]="'md'">
          <header hlmDialogHeader>
            <h2 hlmDialogTitle tabindex="-1">{{ copy.details }}</h2>
            <p hlmDialogDescription hlmDialogSubject>
              <bdi>{{ node().title }}</bdi>
            </p>
          </header>
          <dl class="hlm-field-grid">
            <div>
              <dt>{{ copy.children }}</dt>
              <dd><app-number-value [value]="node().child_count" /></dd>
            </div>
            <div>
              <dt>{{ copy.items }}</dt>
              <dd><app-number-value [value]="node().item_count" /></dd>
            </div>
            <div class="hlm-field-wide">
              <dt>{{ copy.pending }}</dt>
              <dd>{{ copy.pendingReason }}</dd>
            </div>
            <div class="hlm-field-wide">
              <dt>{{ copy.nodeId }}</dt>
              <dd>
                <app-copyable-identifier
                  [value]="node().node_id"
                  [label]="copy.nodeId"
                  [truncate]="false"
                  [copySize]="'lg'"
                />
              </dd>
            </div>
          </dl>
          <footer hlmDialogFooter>
            <button hlmBtn hlmDialogClose type="button" [variant]="'outline'" i18n="@@ui.close">
              Close
            </button>
          </footer>
        </hlm-dialog-content>
      </hlm-dialog>
    }
  `,
  styles: `
    @media (pointer: coarse) {
      button[appRowAction] {
        min-inline-size: var(--eg-control-touch);
        min-block-size: var(--eg-control-touch);
      }
    }
  `,
})
export class AuthoringNodeDetailsComponent {
  readonly node = input.required<NodeDto>();
  protected readonly copy = AUTHORING_TREE_COPY;
  protected readonly label = computed(() => `${this.copy.details} · ${this.node().title}`);
  protected readonly open = linkedSignal<string, boolean>({
    source: () => this.node().node_id,
    computation: () => false,
  });
}
