import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import type { NavGroupView } from '../nav/nav.models';
import { ShellIconComponent } from './shell-icon.component';

/** Consumes the already-filtered nav view; no permission or phase decisions belong here. */
@Component({
  selector: 'app-shell-nav',
  imports: [RouterLink, RouterLinkActive, ShellIconComponent, HlmButton, HlmBadge, HlmTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nav.component.html',
  host: {
    class:
      'group/nav block min-h-0 overflow-y-auto overscroll-contain scroll-py-(--eg-space-3) px-(--eg-space-4) data-[collapsed=true]:px-(--eg-space-3) pb-(--eg-space-5) [scrollbar-width:thin]',
    '[class.rail]': 'collapsed()',
    '[attr.data-collapsed]': 'collapsed()',
  },
})
export class NavComponent {
  readonly groups = input<readonly NavGroupView[]>([]);
  readonly collapsed = input(false);
  readonly itemSelected = output<readonly string[]>();
  readonly navigationLabel = $localize`:@@shell.layout.primary-navigation:Primary navigation`;
}
