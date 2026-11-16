import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { Component, inject, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { BehaviorSubject } from 'rxjs';
import { DesktopMutationDirective } from './desktop-mutation.directive';
import { DesktopRequiredNoticeComponent } from './desktop-required-notice.component';
import { DESKTOP_MUTATION_QUERY, NarrowMutationPolicy } from './narrow-mutation-policy';

@Component({
  selector: 'app-narrow-policy-test',
  imports: [HlmButton, HlmInput, DesktopMutationDirective, DesktopRequiredNoticeComponent],
  template: `
    <app-desktop-required-notice />
    <p data-facts>{{ fact }}</p>
    <a href="#inspection">{{ inspect }}</a>
    <button hlmBtn type="button" data-inspect (click)="inspections = inspections + 1">
      {{ inspect }}
    </button>
    <form *appDesktopMutation novalidate (submit)="submit($event)">
      <input hlmInput [value]="draft()" (input)="edit($event)" />
      <button hlmBtn type="submit">{{ save }}</button>
    </form>
  `,
})
class NarrowPolicyTestComponent {
  readonly policy = inject(NarrowMutationPolicy);
  readonly authorized = signal(true);
  readonly draft = signal('Retained draft');
  readonly fact = 'Effective policy: inherited';
  readonly inspect = 'Inspect';
  readonly save = 'Save';
  mutations = 0;
  inspections = 0;

  edit(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.draft.set(event.target.value);
  }

  submit(event?: Event): void {
    event?.preventDefault();
    if (!this.policy.allowsMutation() || !this.authorized()) return;
    this.mutations += 1;
  }
}

describe('shared narrow mutation policy', () => {
  let width: number;
  let states: BehaviorSubject<BreakpointState>;
  let fixture: ComponentFixture<NarrowPolicyTestComponent>;
  let root: HTMLElement;

  beforeEach(() => {
    width = 390;
    states = new BehaviorSubject<BreakpointState>({
      matches: false,
      breakpoints: { [DESKTOP_MUTATION_QUERY]: false },
    });
    TestBed.configureTestingModule({
      imports: [NarrowPolicyTestComponent],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: BreakpointObserver,
          useValue: {
            observe: (query: string) => {
              expect(query).toBe('(min-width: 768px)');
              return states.asObservable();
            },
            isMatched: (query: string) => query === DESKTOP_MUTATION_QUERY && width >= 768,
          },
        },
      ],
    });
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  async function render(viewport = 390): Promise<void> {
    resize(viewport);
    fixture = TestBed.createComponent(NarrowPolicyTestComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    root = fixture.nativeElement as HTMLElement;
  }

  function resize(viewport: number): void {
    width = viewport;
    states.next({ matches: width >= 768, breakpoints: { [DESKTOP_MUTATION_QUERY]: width >= 768 } });
  }

  it('keeps 390px inspection, copyable facts and navigation without mutation controls', async () => {
    await render(390);
    expect(fixture.componentInstance.policy.narrow()).toBe(true);
    expect(root.querySelector('form, input, button[type="submit"]')).toBeNull();
    expect(root.querySelector('[data-facts]')?.textContent).toContain('inherited');
    expect(root.querySelector('a')?.getAttribute('href')).toBe('#inspection');
    root.querySelector<HTMLButtonElement>('[data-inspect]')!.click();
    expect(fixture.componentInstance.inspections).toBe(1);
    const notice = root.querySelector('section')!;
    expect(notice.getAttribute('data-slot')).toBe('alert');
    expect(notice.getAttribute('role')).toBe('note');
    expect(notice.textContent).toContain('Desktop required');
    expect(root.querySelector('[role="alert"], [aria-live]')).toBeNull();
    fixture.componentInstance.submit();
    expect(fixture.componentInstance.mutations).toBe(0);
  });

  it('allows 1024px mutation only with the existing permission check', async () => {
    await render(1024);
    expect(fixture.componentInstance.policy.canMutate()).toBe(true);
    expect(root.querySelector('form')).not.toBeNull();
    expect(root.querySelector('section')).toBeNull();
    fixture.componentInstance.authorized.set(false);
    fixture.componentInstance.submit();
    expect(fixture.componentInstance.mutations).toBe(0);
    fixture.componentInstance.authorized.set(true);
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    expect(fixture.componentInstance.mutations).toBe(1);
  });

  it('uses the existing 768px boundary without a second mobile breakpoint', async () => {
    await render(767);
    expect(root.querySelector('form')).toBeNull();
    resize(768);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('form')).not.toBeNull();
    expect(fixture.componentInstance.policy.allowsMutation()).toBe(true);
  });

  it('retains page-owned drafts across resize and never hides inspection', async () => {
    await render(1024);
    const input = root.querySelector('input')!;
    input.value = 'Bản nháp vẫn được giữ lại';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    resize(390);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('form')).toBeNull();
    expect(root.querySelector('[data-facts]')).not.toBeNull();
    resize(1024);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('input')?.value).toBe('Bản nháp vẫn được giữ lại');
  });

  it('guards a handler with current viewport state before the responsive signal catches up', async () => {
    await render(1024);
    width = 390;
    expect(fixture.componentInstance.policy.canMutate()).toBe(true);
    fixture.componentInstance.submit();
    expect(fixture.componentInstance.mutations).toBe(0);
  });

  it('unsubscribes the shared observer when its injection context is destroyed', () => {
    const policy = TestBed.inject(NarrowMutationPolicy);
    expect(policy.narrow()).toBe(true);
    expect(states.observed).toBe(true);
    TestBed.resetTestingModule();
    expect(states.observed).toBe(false);
  });

  for (const theme of ['light', 'dark']) {
    it(`retains the same untruncated notice composition in ${theme}`, async () => {
      document.documentElement.setAttribute('data-theme', theme);
      await render(390);
      expect(root.querySelector('[data-slot="alert-title"]')?.textContent).toBe('Desktop required');
      expect(root.querySelector('[data-slot="alert-description"]')?.textContent).toContain(
        'You can still inspect information, copy values and navigate.',
      );
      expect(root.querySelector('section')?.classList.contains('wrap-anywhere')).toBe(true);
      expect(root.querySelector('[title], .truncate')).toBeNull();
    });
  }
});
