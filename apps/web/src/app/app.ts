import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * Root component of the application shell. 09.1.1-T4 replaces the single heading with the
 * layout (header, tenant/project switcher, router outlet); until then it exists so the
 * bundle, the zoneless TestBed and the E2E smoke test have a real Angular tree to render.
 */
@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
