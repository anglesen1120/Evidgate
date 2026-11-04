import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * Root component of the application: it mounts the router outlet and nothing else. Which layout a
 * URL gets is `app.routes.ts`'s decision — the shell (`ShellComponent`) for everything signed in,
 * the sign-in placeholder outside it — so this component stays a router host and a page never
 * nests inside a component that also renders one. 09.1.1-T4.1 replaced the single heading the
 * placeholder app had.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-h-full' },
})
export class App {}
