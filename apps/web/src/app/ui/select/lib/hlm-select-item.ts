import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmIcon } from '@evidgate/ui/icon';
import { BrnSelectItem } from '@spartan-ng/brain/select';
import { classes } from '@evidgate/ui/utils';

@Component({
  selector: 'hlm-select-item',
  exportAs: 'hlmSelectItem',
  imports: [HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  hostDirectives: [{ directive: BrnSelectItem, inputs: ['id', 'disabled', 'value'] }],
  host: { 'data-slot': 'select-item' },
  template: `
    <ng-content />
    @if (_active()) {
      <hlm-icon
        [name]="'lucideCheck'"
        class="spartan-select-item-indicator"
        [attr.aria-hidden]="'true'"
      />
    }
  `,
})
export class HlmSelectItem {
  private readonly _brnSelectItem = inject(BrnSelectItem);

  protected readonly _active = this._brnSelectItem.active;

  constructor() {
    classes(
      () =>
        'spartan-select-item relative flex w-full cursor-default items-center outline-hidden select-none data-disabled:pointer-events-none data-disabled:opacity-50 [&_hlm-icon]:pointer-events-none [&_hlm-icon]:shrink-0',
    );
  }
}
