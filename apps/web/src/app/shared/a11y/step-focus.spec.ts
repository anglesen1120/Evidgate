import {
  ChangeDetectionStrategy,
  Component,
  type ElementRef,
  type Type,
  provideZonelessChangeDetection,
  signal,
  viewChild,
} from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';

import { focusRefusalHeading, focusWaitingLine, requestRefusalFocus } from './step-focus';

/** The request only asks whether the press failed, so any error value arms it. */
const REFUSAL = { kind: 'conflict' };

/** A step that renders a waiting line, and a refusal heading only while it shows a refusal. */
@Component({
  selector: 'app-step-focus-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p #waitingLine tabindex="-1" data-testid="waiting">{{ waitingText }}</p>
    @if (refusal()) {
      @if (headingMounted()) {
        <h2 #refusalHeading tabindex="-1" data-testid="refusal">{{ refusalText }}</h2>
      }
    }
  `,
})
class StepFocusHost {
  protected readonly waitingText = 'Enabling collection';
  protected readonly refusalText = 'Collection could not be enabled';
  readonly refusal = signal(false);
  /** A refusal can be known before the pass that renders its heading, as a load does. */
  readonly headingMounted = signal(true);
  readonly request = signal(false);
  private readonly waitingLine = viewChild.required<ElementRef<HTMLElement>>('waitingLine');
  private readonly refusalHeading = viewChild<ElementRef<HTMLElement>>('refusalHeading');

  constructor() {
    focusRefusalHeading({
      request: this.request,
      heading: this.refusalHeading,
      shown: () => this.refusal(),
    });
  }

  holdWaitingLine(): void {
    focusWaitingLine(this.waitingLine);
  }
}

/** The after-render variant: no `shown`, and a request spent even when nothing rendered. */
@Component({
  selector: 'app-step-focus-after-render-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (refusal()) {
      <h2 #refusalHeading tabindex="-1" data-testid="refusal">{{ refusalText }}</h2>
    }
  `,
})
class AfterRenderFocusHost {
  protected readonly refusalText = 'Collection could not be enabled';
  readonly refusal = signal(false);
  readonly request = signal(false);
  private readonly refusalHeading = viewChild<ElementRef<HTMLElement>>('refusalHeading');

  constructor() {
    focusRefusalHeading({
      request: this.request,
      heading: this.refusalHeading,
      afterRender: true,
    });
  }
}

async function render<T>(component: Type<T>): Promise<ComponentFixture<T>> {
  await TestBed.configureTestingModule({
    imports: [component],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  return fixture;
}

/** The element focus is on, or `null` for `<body>`. */
function focused(): Element | null {
  return document.activeElement === document.body ? null : document.activeElement;
}

describe('focusWaitingLine', () => {
  it('moves the place to the waiting line and leaves focus alone when none is mounted', async () => {
    const fixture = await render(StepFocusHost);
    const root = fixture.nativeElement as HTMLElement;
    const waiting = root.querySelector('[data-testid="waiting"]');

    fixture.componentInstance.holdWaitingLine();
    expect(focused()).toBe(waiting);

    // A target the pass has not mounted is no reason to drop the place the visitor already has.
    const elsewhere = document.createElement('button');
    document.body.append(elsewhere);
    elsewhere.focus();
    focusWaitingLine(signal<ElementRef<HTMLElement> | undefined>(undefined));
    expect(focused()).toBe(elsewhere);
    elsewhere.remove();
  });
});

describe('requestRefusalFocus', () => {
  it('arms the request only for a refusal', () => {
    const request = signal(false);
    requestRefusalFocus(request, null);
    expect(request()).toBe(false);
    requestRefusalFocus(request, undefined);
    expect(request()).toBe(false);
    requestRefusalFocus(request, REFUSAL);
    expect(request()).toBe(true);
  });
});

describe('focusRefusalHeading', () => {
  it('holds the waiting line until the refusal heading mounts, then moves focus to it', async () => {
    const fixture = await render(StepFocusHost);
    const host = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;
    host.headingMounted.set(false);
    host.holdWaitingLine();

    host.refusal.set(true);
    requestRefusalFocus(host.request, REFUSAL);
    await fixture.whenStable();
    // The refusal is shown but its heading is not mounted yet: the request stays armed, so the pass
    // that mounts the heading spends it instead of stranding the visitor on the waiting line.
    expect(host.request()).toBe(true);
    expect(focused()).toBe(root.querySelector('[data-testid="waiting"]'));

    host.headingMounted.set(true);
    await fixture.whenStable();
    expect(focused()).toBe(root.querySelector('[data-testid="refusal"]'));
    expect(host.request()).toBe(false);
  });

  it('drops a request whose refusal the page no longer shows', async () => {
    const fixture = await render(StepFocusHost);
    const host = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;
    host.holdWaitingLine();

    requestRefusalFocus(host.request, REFUSAL);
    await fixture.whenStable();
    expect(host.request()).toBe(false);
    expect(focused()).toBe(root.querySelector('[data-testid="waiting"]'));

    // The refusal renders after the request was dropped, so it must not take the place back.
    host.refusal.set(true);
    await fixture.whenStable();
    expect(focused()).toBe(root.querySelector('[data-testid="waiting"]'));
  });

  it('spends a request once, so a later refusal render does not move focus again', async () => {
    const fixture = await render(StepFocusHost);
    const host = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;
    host.refusal.set(true);
    await fixture.whenStable();
    host.holdWaitingLine();

    requestRefusalFocus(host.request, REFUSAL);
    await fixture.whenStable();
    expect(focused()).toBe(root.querySelector('[data-testid="refusal"]'));

    host.holdWaitingLine();
    host.refusal.set(false);
    await fixture.whenStable();
    host.refusal.set(true);
    await fixture.whenStable();
    expect(focused()).toBe(root.querySelector('[data-testid="waiting"]'));
  });

  it('consumes an after-render request whose heading never renders', async () => {
    const fixture = await render(AfterRenderFocusHost);
    const host = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;

    requestRefusalFocus(host.request, REFUSAL);
    await fixture.whenStable();
    expect(host.request()).toBe(false);
    expect(focused()).toBeNull();

    // The spent request cannot fire on a later render; the next one moves focus once.
    host.refusal.set(true);
    await fixture.whenStable();
    expect(focused()).toBeNull();
    requestRefusalFocus(host.request, REFUSAL);
    await fixture.whenStable();
    expect(focused()).toBe(root.querySelector('[data-testid="refusal"]'));
  });
});
