import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The sign-in placeholder until 09.1.1-T5 ships the real form.
 *
 * It carries no action on purpose (review D2). Its only audience is an anonymous visitor, and
 * `landing.guard.ts` sends that visitor here from `/`, so every link back to the workspace would
 * land on this same page — the QA repro followed "Back to workspace" to `/` and back to `/login`.
 * T5 owns the action, because only a real sign-in can move the visitor forward.
 */
@Component({
  selector: 'app-sign-in-required-page',
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
    </section>
  </main>`,
})
export class SignInRequiredPage {
  readonly heading = $localize`:@@shell.page.sign-in.heading:Sign in required`;
  readonly explanation = $localize`:@@shell.page.sign-in.explanation:Sign-in is not available in this workspace yet.`;
}
