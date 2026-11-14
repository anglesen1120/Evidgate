import {
  afterNextRender,
  afterRenderEffect,
  effect,
  type ElementRef,
  type Injector,
  type Signal,
  type WritableSignal,
} from '@angular/core';

/** A page element a step hands focus to; `undefined` until the pass that mounts it has run. */
export type FocusTarget = Signal<ElementRef<HTMLElement> | undefined>;

/**
 * Hands the place to the line that states a step is waiting, before the pressed control is disabled
 * and focus falls to `<body>`. The line stays mounted while the command runs, so it holds the place
 * until the answer hands it on.
 */
export function focusWaitingLine(target: FocusTarget): void {
  target()?.nativeElement.focus();
}

/**
 * Arms the refusal-focus request when a press of the page's own command ended with `error`. Only the
 * page's own presses reach it, so a background re-read that fails on its own never moves focus.
 */
export function requestRefusalFocus(request: WritableSignal<boolean>, error: unknown): void {
  if (error === null || error === undefined) return;
  request.set(true);
}

/** What a step tells {@link focusFieldAfterRender} about the control it is looking for. */
export interface FieldFocusOptions {
  readonly injector: Injector;
  readonly host: ElementRef<HTMLElement>;
  /** The id of the control the refusal belongs to. */
  readonly id: string;
  /** Whether the page still shows the scope the validation belonged to. */
  readonly current: () => boolean;
}

/**
 * Moves focus to `#id` in the render pass that mounts the flagged control, so a control rendered
 * after validation still receives the place. A page that moved on to another scope drops the move.
 */
export function focusFieldAfterRender(options: FieldFocusOptions): void {
  afterNextRender(
    () => {
      if (options.current()) {
        options.host.nativeElement.querySelector<HTMLElement>(`#${options.id}`)?.focus();
      }
    },
    { injector: options.injector },
  );
}

/** What a step tells {@link focusRefusalHeading} about the refusal it is showing. */
export interface RefusalFocusOptions {
  readonly request: WritableSignal<boolean>;
  readonly heading: FocusTarget;
  /**
   * Whether the page still renders the refusal the request belongs to. A request whose refusal is
   * gone is dropped, because a request kept for a later render would move focus nobody pressed for.
   */
  readonly shown?: () => boolean;
  /**
   * Spend the request in the render pass that mounts the heading (`afterRenderEffect`) instead of on
   * the next effect run. Such a request is consumed even when no heading renders, so it cannot fire
   * on a later refusal.
   */
  readonly afterRender?: boolean;
}

/**
 * Creates the pass that spends a refusal-focus request on the heading, and never twice: the request
 * is cleared before focus moves. Call it from a constructor, so the pass belongs to the step.
 *
 * A heading that has not mounted leaves the request armed and keeps the ref tracked, which is what
 * lets the pass that mounts it spend the request instead of stranding the visitor on `<body>`.
 */
export function focusRefusalHeading(options: RefusalFocusOptions): void {
  const spend = (): void => {
    if (!options.request()) return;
    if (options.shown !== undefined && !options.shown()) {
      options.request.set(false);
      return;
    }
    const heading = options.heading()?.nativeElement;
    if (heading === undefined) {
      if (options.afterRender === true) {
        options.request.set(false);
      }
      return;
    }
    options.request.set(false);
    heading.focus();
  };
  if (options.afterRender === true) {
    afterRenderEffect({ write: spend });
  } else {
    effect(spend);
  }
}
