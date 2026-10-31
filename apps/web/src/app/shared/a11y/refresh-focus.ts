import { signal } from '@angular/core';

/**
 * Keeps the visitor's place across a re-read they asked for.
 *
 * A re-read can remove the very control the press was made with — the retry notice goes away with
 * it — and the browser then drops focus on `<body>`. The handoff records where the visitor was
 * before the read started, and decides after the DOM has committed whether that place still exists.
 * A control that survived, a failed read that leaves it standing, and a focus the visitor moved
 * themselves all keep them where they are: only a place the read actually took earns a move.
 */

/**
 * The place a re-read may take away, captured before the read and spent after the commit.
 */
export interface RefreshFocusHandoff {
  /** The control the press was made with. */
  readonly control: HTMLElement;
  /** The panel the control belongs to: its heading takes the place when the control is gone. */
  readonly panel: 'binding' | 'token';
  /** The scope the press was made in; a read that answers another scope moves no focus. */
  readonly version: number;
  /** Set by a focus that moved after the press; that focus belongs to the visitor and is kept. */
  superseded: boolean;
  readonly watch: (event: FocusEvent) => void;
}

/**
 * Records the control a re-read may take away, or `null` when focus is not on one of the page's own
 * controls: nothing of ours was taken away, so nothing is owed.
 *
 * `panelOf` names the panel a control belongs to, because the heading that takes the place back is
 * the one of the panel the control was part of.
 */
export function captureRefreshFocus(
  document: Document,
  host: HTMLElement,
  version: number,
  panelOf: (control: HTMLElement) => 'binding' | 'token',
): RefreshFocusHandoff | null {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement) || active === document.body) return null;
  if (!host.contains(active)) return null;
  const handoff: RefreshFocusHandoff = {
    control: active,
    panel: panelOf(active),
    version,
    superseded: false,
    watch: (event: FocusEvent): void => {
      // A focus that moved after the press is the visitor's own: it is never taken back.
      if (event.target !== active) handoff.superseded = true;
    },
  };
  document.addEventListener('focusin', handoff.watch, true);
  return handoff;
}

/** Stops watching a handoff, whether it is spent or abandoned. */
export function releaseRefreshFocus(document: Document, handoff: RefreshFocusHandoff): void {
  document.removeEventListener('focusin', handoff.watch, true);
}

/**
 * Puts the visitor's place back, but only when the re-read took it.
 *
 * `headings` is resolved by the caller so a heading that a render has not mounted yet is simply
 * absent, and a handoff that answers another scope (`version`) moves nothing.
 */
export function restoreRefreshFocus(
  document: Document,
  handoff: RefreshFocusHandoff,
  version: number,
  headings: { readonly binding: HTMLElement | undefined; readonly token: HTMLElement | undefined },
): void {
  if (handoff.version !== version) return;
  const active = document.activeElement;
  const control = handoff.control;
  if (control.isConnected && !control.matches(':disabled')) {
    // Still usable, so it is still where the visitor was: only a focus the read itself dropped on
    // `<body>` is taken back, while a focus the visitor moved is left alone.
    if (active === null || active === document.body) control.focus();
    return;
  }
  if (handoff.superseded) return;
  // Only a focus that moved to another control is the visitor's own; focus still on the control
  // the read made unusable, or dropped on `<body>`, is the place this handoff puts back.
  if (active !== null && active !== document.body && active !== control) return;
  (handoff.panel === 'token' ? headings.token : headings.binding)?.focus();
}

/**
 * Holds one page's re-read place from the press until the pass that spends it, so a page destroyed
 * mid-read still releases the document listener instead of leaking it.
 */
export class RefreshFocusSession {
  private pending: RefreshFocusHandoff | null = null;
  private readonly spent = signal<RefreshFocusHandoff | null>(null);

  constructor(private readonly document: Document) {}

  /** Records the place the read may take; a read with nothing of the page's focused holds none. */
  capture(
    host: HTMLElement,
    version: number,
    panelOf: (control: HTMLElement) => 'binding' | 'token',
  ): void {
    this.pending = captureRefreshFocus(this.document, host, version, panelOf);
  }

  /** Moves the held place on to the pass that spends it, once the read has settled. */
  settle(): void {
    const handoff = this.pending;
    this.pending = null;
    if (handoff !== null) this.spent.set(handoff);
  }

  /** Takes the settled place for the render pass that spends it, or `null` when there is none. */
  spend(): RefreshFocusHandoff | null {
    const handoff = this.spent();
    if (handoff !== null) this.spent.set(null);
    return handoff;
  }

  /** Releases whatever is still held, for a page destroyed before either path could spend it. */
  release(): void {
    const pending = this.pending;
    this.pending = null;
    if (pending !== null) releaseRefreshFocus(this.document, pending);
    const spent = this.spend();
    if (spent !== null) releaseRefreshFocus(this.document, spent);
  }
}
