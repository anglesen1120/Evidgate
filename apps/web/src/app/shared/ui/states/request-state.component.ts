// State sheet: docs/design/web/state-sheet.md#error-pattern
import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  effect,
  inject,
  Injector,
  input,
  output,
} from '@angular/core';

import type { ApiError } from '../../../core/http/api-error';
import { RetryAfterComponent } from '../conflict';
import { EmptyPatternComponent } from './empty-pattern.component';
import { ErrorPatternComponent } from './error-pattern.component';
import { ForbiddenPatternComponent } from './forbidden-pattern.component';
import { LoadingPatternComponent } from './loading-pattern.component';
import { NotFoundPatternComponent } from './not-found-pattern.component';
import { patternForError } from './state-pattern';

@Component({
  selector: 'app-request-state',
  standalone: true,
  imports: [
    LoadingPatternComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    NotFoundPatternComponent,
    RetryAfterComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './request-state.component.css',
  host: { '(focusin)': 'noteFocusIn()' },
  template: `
    @switch (status()) {
      @case ('loading') {
        <app-loading-pattern />
      }
      @case ('error') {
        @switch (pattern()) {
          @case ('forbidden') {
            <app-forbidden-pattern />
          }
          @case ('not_found') {
            <app-not-found-pattern />
          }
          @default {
            @if (retryAfterError(); as failure) {
              <app-retry-after
                [kind]="failure.kind === 'unavailable' ? 'unavailable' : 'rate_limited'"
                [retryAfterSeconds]="failure.retryAfterSeconds"
                [retryEligible]="retryEligible()"
                [busy]="retryBusy()"
                [code]="failure.code"
                [requestId]="failure.requestId"
                (retry)="retry.emit()"
              />
            } @else {
              <app-error-pattern
                [error]="error()"
                [retryDisabled]="!retryEligible() || retryBusy()"
                (retry)="retry.emit()"
              />
            }
          }
        }
      }
      @case ('ready') {
        @if (empty()) {
          <app-empty-pattern
            [title]="emptyTitle()"
            [description]="emptyDescription()"
            [actionLabel]="emptyActionLabel()"
            [actionHref]="emptyActionHref()"
            (action)="emptyAction.emit()"
          />
        } @else {
          <ng-content />
        }
      }
    }
  `,
})
export class RequestStateComponent {
  readonly status = input<'loading' | 'ready' | 'error'>('loading');
  readonly error = input<ApiError | null>(null);
  readonly empty = input(false);
  readonly emptyTitle = input('');
  readonly emptyDescription = input<string | undefined>();
  readonly emptyActionLabel = input<string | undefined>();
  readonly emptyActionHref = input<string | undefined>();
  readonly retryEligible = input(false);
  readonly retryBusy = input(false);
  readonly retry = output<void>();
  readonly emptyAction = output<void>();

  protected readonly pattern = computed(() => patternForError(this.error()));
  protected readonly retryAfterError = computed(() => {
    const failure = this.error();
    return failure?.kind === 'rate_limited' ||
      (failure?.kind === 'unavailable' && failure.retryAfterSeconds !== null)
      ? failure
      : null;
  });

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  /** The status the replacement effect saw last, so only a move out of `error` reacts. */
  private lastStatus: 'loading' | 'ready' | 'error' | null = null;
  /**
   * Focus reached this replacement during the current failure: its own panels or the page content
   * it projects. Ownership is scoped to one failure — a new failure starts unowned and a recovery
   * spends the claim — so an earlier visit cannot pull focus off `body` later.
   */
  private hadFocus = false;
  /** A handoff is queued for the render in which the failure panel is gone. */
  private handoff = false;
  /** Where this component last sent focus, while that loan is still the place holding it. */
  private parked: HTMLElement | null = null;
  /** A return to the retry control is queued for the render in which it answers again. */
  private resume = false;

  constructor() {
    // The visitor's Retry is inside the panel this component replaces, so activating it destroys
    // the control that held focus and the document drops focus to `body`: the page then announces
    // the recovered content from the top while the visitor's place is nowhere. Hand the focus the
    // replacement owned to the page heading (or, with no heading, the `main` region) as soon as
    // the panel leaves. A visitor who moved elsewhere keeps their own focus: the handoff is only
    // queued while this component owned it, and it is dropped if focus landed outside by then.
    effect(() => {
      const status = this.status();
      const previous = this.lastStatus;
      this.lastStatus = status;
      // A failure is the only place this component may claim focus: entering one starts with
      // nothing owned, so focus left on `body` at that point is the visitor's own choice.
      if (status === 'error' && previous !== 'error') this.hadFocus = false;
      if (previous !== 'error' || status === 'error') return;
      // Leaving `error` spends the claim, whether or not the handoff takes it.
      const claimed = this.hadFocus;
      this.hadFocus = false;
      // A settled recovery ends the loan: focus belongs to the page again.
      if (status === 'ready') this.parked = null;
      if (!claimed || this.focusElsewhere()) return;
      this.handoff = true;
      afterNextRender(() => this.finishHandoff(), { injector: this.injector });
    });
    // A re-failure disables the very control the visitor used to ask for it — its new Retry-After
    // is not up yet — so that panel's only control becomes unfocusable in place. The place moves on
    // like any other loss instead of resting on a control that no longer answers.
    effect(() => {
      const unavailable = !this.retryEligible() || this.retryBusy();
      if (!unavailable || this.status() !== 'error') return;
      const active: unknown = this.document.activeElement;
      if (!(active instanceof HTMLElement) || !this.host.nativeElement.contains(active)) return;
      this.handoff = true;
      afterNextRender(() => this.finishHandoff(), { injector: this.injector });
    });
    // That move is a loan, not a landing: while the control the visitor asked with answers again
    // in the same failure, the place this component borrowed comes back to it — unless focus has
    // moved somewhere the visitor chose since, in which case it is left alone.
    effect(() => {
      const answers = this.status() === 'error' && this.retryEligible() && !this.retryBusy();
      const parked = this.parked;
      if (!answers || parked === null || !parked.isConnected) return;
      if (this.document.activeElement !== parked) return;
      this.resume = true;
      afterNextRender(() => this.finishResume(), { injector: this.injector });
    });
  }

  /** Records that focus is inside this replacement; a host listener because panels leave the DOM. */
  protected noteFocusIn(): void {
    this.hadFocus = true;
  }

  /** True when a real element outside this replacement holds focus, i.e. the visitor moved on. */
  private focusElsewhere(): boolean {
    const active: unknown = this.document.activeElement;
    // A removed panel's control is no longer connected: that is the loss this handoff repairs,
    // not a visitor who moved elsewhere.
    return (
      active instanceof HTMLElement &&
      active !== this.document.body &&
      active.isConnected &&
      !this.host.nativeElement.contains(active)
    );
  }

  /** Moves focus to the page heading (else its `main` region) once, and only if still unowned. */
  private finishHandoff(): void {
    if (!this.handoff) return;
    this.handoff = false;
    if (this.focusElsewhere()) return;
    const target = this.handoffTarget();
    if (target === null) return;
    // A heading is not focusable on its own; `-1` makes it programmatically focusable without
    // adding a Tab stop, and the attribute is removed again when focus moves on (so the page keeps
    // its own markup). Only an attribute this component added is removed.
    const addedTabIndex = !target.hasAttribute('tabindex');
    if (addedTabIndex) target.setAttribute('tabindex', '-1');
    target.focus();
    if (addedTabIndex) {
      target.addEventListener('blur', () => target.removeAttribute('tabindex'), { once: true });
    }
    // Remember the loan, so a control that answers again in the same failure gets the place back.
    this.parked = target;
    // The claim is spent with the move: focus belongs to the page until it comes back inside.
    this.hadFocus = false;
  }

  /** Returns the loaned place to the retry control, only while focus is still where this put it. */
  private finishResume(): void {
    if (!this.resume) return;
    this.resume = false;
    const parked = this.parked;
    this.parked = null;
    // Anything but the loan still holding focus is where the visitor chose to be: leave it there.
    if (parked === null || this.document.activeElement !== parked) return;
    const control = this.retryControl();
    if (control === null) return;
    control.focus();
  }

  /** The failure panel's own retry control, once it answers again; the panel's only button. */
  private retryControl(): HTMLElement | null {
    return (
      Array.from(
        this.host.nativeElement.querySelectorAll<HTMLButtonElement>('section button'),
      ).find((control) => !control.disabled) ?? null
    );
  }

  private handoffTarget(): HTMLElement | null {
    const region =
      this.host.nativeElement.closest('main, [role="main"]') ??
      this.document.querySelector('main, [role="main"]');
    const heading = region?.querySelector('h1') ?? this.document.querySelector('h1');
    if (heading instanceof HTMLElement) return heading;
    return region instanceof HTMLElement ? region : null;
  }
}
