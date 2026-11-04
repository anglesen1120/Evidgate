import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  inject,
  output,
  viewChild,
} from '@angular/core';
import { HlmCardTitle } from '@evidgate/ui/card';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { MEMBERS_COPY } from './member-list.component';

/** Reads only its parent dialog's ephemeral slot; no second copy of the issued value. */
@Component({
  selector: 'app-invite-reveal',
  imports: [ConnectStepRevealComponent, HlmButton, HlmDialogImports, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-4 wrap-anywhere' },
  template: `
    @if (slot.current(); as entry) {
      <div class="dialog-body grid min-w-0 gap-3">
        <app-connect-step-reveal
          [heading]="copy.revealTitle"
          [value]="entry.value"
          [warning]="copy.revealWarning"
          [copyLabel]="copy.copy"
          [copiedLabel]="copy.copied"
          [dismissLabel]="copy.dismiss"
          (dismissed)="dismissed.emit()"
        />
      </div>
    } @else {
      <header hlmDialogHeader>
        <h2 hlmCardTitle #notice tabindex="-1">{{ copy.replayTitle }}</h2>
      </header>
      <div class="dialog-body grid min-w-0 gap-3" data-members-invite-replay role="status">
        {{ copy.replayNotice }}
      </div>
      <footer hlmDialogFooter>
        <button hlmBtn [variant]="'outline'" type="button" (click)="dismissed.emit()">
          {{ copy.close }}
        </button>
      </footer>
    }
  `,
})
export class InviteRevealComponent {
  protected readonly slot = inject(RevealSlot);
  protected readonly copy = MEMBERS_COPY;
  readonly dismissed = output<void>();
  private readonly notice = viewChild<ElementRef<HTMLHeadingElement>>('notice');

  constructor() {
    afterRenderEffect(() => this.notice()?.nativeElement.focus());
  }
}
