import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-sign-in-required-page',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../layout/shell-page.css'],
  styles: `
    :host {
      box-sizing: border-box;
      display: grid;
      place-items: center;
      min-height: 100dvh;
      padding: var(--eg-space-5);
      background: var(--eg-bg);
    }
    main {
      min-width: 0;
      width: 100%;
      max-width: calc(2 * var(--eg-rail-width));
    }
  `,
  template: `<main>
    <section>
      <h1>{{ heading }}</h1>
      <p>{{ explanation }}</p>
      <a routerLink="/">{{ action }}</a>
    </section>
  </main>`,
})
export class SignInRequiredPage {
  readonly heading = $localize`:@@shell.page.sign-in.heading:Sign in required`;
  readonly explanation = $localize`:@@shell.page.sign-in.explanation:Sign-in is not available in this workspace yet.`;
  readonly action = $localize`:@@shell.page.sign-in.action:Back to workspace`;
}
