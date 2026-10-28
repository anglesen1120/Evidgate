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
  /** Focus reached this replacement at some point: its own panels or the content it projects. */
  private hadFocus = false;
  /** A handoff is queued for the render in which the failure panel is gone. */
  private handoff = false;

  constructor() {
    // The visitor's Retry is inside the panel this component replaces, so activating it destroys
    // the control that held focus and the document drops focus to `body`: the page then announces
    // the recovered content from the top while the visitor's place is nowhere. Hand the focus the
    // replacement owned to the page heading (or, with no heading, the `main` region) as soon as
    // the panel leaves. A visitor who moved elsewhere keeps their own focus: the handoff is only
    // queued while this component owned it, and it is dropped if focus landed outside by then.
    effect(() => {
      const status = this.status();
      const replaced = this.lastStatus === 'error' && status !== 'error';
      this.lastStatus = status;
      if (!replaced || !this.hadFocus || this.focusElsewhere()) return;
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
