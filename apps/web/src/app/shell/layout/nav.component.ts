import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import type { NavGroupView } from '../nav/nav.models';
import { ShellIconComponent } from './shell-icon.component';

/** Consumes the already-filtered nav view; no permission or phase decisions belong here. */
@Component({
  selector: 'app-shell-nav',
  imports: [RouterLink, RouterLinkActive, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './nav.component.html',
  styleUrl: './nav.component.css',
  host: { '[class.rail]': 'collapsed()' },
})
export class NavComponent {
  readonly groups = input<readonly NavGroupView[]>([]);
  readonly collapsed = input(false);
  readonly itemSelected = output<void>();
  readonly navigationLabel = $localize`:@@shell.layout.primary-navigation:Primary navigation`;
}
