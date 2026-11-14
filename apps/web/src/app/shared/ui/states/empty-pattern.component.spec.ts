// State sheet: docs/design/web/state-sheet.md#empty-pattern
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { EmptyPatternComponent } from './empty-pattern.component';

const rows = [
  ['default', 'implemented — neutral collection'],
  ['hover', 'implemented — primary child only'],
  ['focus-visible', 'implemented — native child only'],
  ['active', 'implemented — explicit native action'],
  ['disabled', 'n/a — no disabled condition is defined (O7)'],
  ['loading', 'n/a — route-level replacement'],
  ['empty', 'implemented — neutral collection'],
  ['error', 'n/a — route-level replacement'],
  ['success', 'n/a — route-level replacement'],
] as const;

describe('EmptyPatternComponent', () => {
  let fixture: ComponentFixture<EmptyPatternComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EmptyPatternComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(EmptyPatternComponent);
    fixture.componentRef.setInput('title', 'No test cases yet');
    fixture.componentRef.setInput('description', 'Create the first case from the feature tree.');
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  it('composes the neutral Helm card with primary button and navigation variants', () => {
    expect(root.querySelector('section')?.getAttribute('data-slot')).toBe('card');
    expect(root.querySelector('[data-slot="card-description"]')?.textContent).toBe(
      'Create the first case from the feature tree.',
    );
    fixture.componentRef.setInput('actionLabel', 'Create case');
    fixture.detectChanges();
    expect(root.querySelector('button')?.getAttribute('data-slot')).toBe('button');
    expect(root.querySelector('button')?.classList.contains('whitespace-normal')).toBe(true);
    fixture.componentRef.setInput('actionHref', '/features');
    fixture.detectChanges();
    expect(root.querySelector('a')?.getAttribute('data-slot')).toBe('button');
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/features');
    expect(root.querySelector('button')).toBeNull();
  });

  for (const [state, contract] of rows) {
    describe(`${state}: ${contract}`, () => {
      it('retains neutral copy, with only an eligible native child action', () => {
        const events: string[] = [];
        fixture.componentInstance.action.subscribe(() => events.push('action'));
        fixture.componentRef.setInput('actionLabel', 'Open feature tree');
        fixture.detectChanges();
        const button = root.querySelector('button')!;
        expect(button.type).toBe('button');
        expect(button.classList.contains('state-panel__action')).toBe(true);
        expect(button.classList.contains('state-panel__action--primary')).toBe(true);
        expect(button.disabled).toBe(false);
        expect(root.querySelector('section')?.hasAttribute('tabindex')).toBe(false);
        expect(root.querySelector('[aria-disabled], [role="alert"], [aria-busy]')).toBeNull();
        expect(root.querySelector('h2')?.textContent).toBe('No test cases yet');
        expect(root.querySelector('[class*="success"]')).toBeNull();
        expect(root.textContent).not.toMatch(/\b(PASS|FAIL|success)\b/);
        if (state === 'hover') {
          button.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
        } else if (state === 'focus-visible') {
          button.focus();
          expect(document.activeElement).toBe(button);
        } else if (state === 'active') {
          button.click();
          expect(events).toEqual(['action']);
        }
        if (state !== 'active') expect(events).toEqual([]);
      });
    });
  }

  it('renders a real navigation anchor only with both label and href, without emitting a command', () => {
    const events: string[] = [];
    fixture.componentInstance.action.subscribe(() => events.push('action'));
    fixture.componentRef.setInput('actionHref', '/features');
    fixture.detectChanges();
    expect(root.querySelector('a, button')).toBeNull();
    fixture.componentRef.setInput('actionLabel', 'Open feature tree');
    fixture.detectChanges();
    const link = root.querySelector('a')!;
    expect(link.getAttribute('href')).toBe('/features');
    expect(link.textContent?.trim()).toBe('Open feature tree');
    expect(root.querySelector('button')).toBeNull();
    // Prevent real navigation in the unit runner, not in the component.
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(events).toEqual([]);
  });

  it('omits absent optional content and never invents a default command', () => {
    fixture.componentRef.setInput('description', undefined);
    fixture.detectChanges();
    expect(root.querySelector('p, a, button')).toBeNull();
  });

  it('keeps long localized text literal without injecting markup', () => {
    const text = 'Chưa có ca kiểm thử '.repeat(40) + '<img src=x onerror=alert(1)>';
    fixture.componentRef.setInput('title', text);
    fixture.componentRef.setInput('description', text);
    fixture.componentRef.setInput('actionLabel', text);
    fixture.detectChanges();
    expect(root.querySelector('h2')?.textContent).toBe(text);
    expect(root.querySelector('img, script, [onerror]')).toBeNull();
    // Helm suppresses first-render transitions until its animation frame restores them.
    for (const element of root.querySelectorAll<HTMLElement>('[style]')) {
      expect(element.hasAttribute('data-slot')).toBe(true);
      expect(element.getAttribute('style')).toBe('transition: none !important;');
    }
  });

  it('renders bare inside an existing card without another boundary or changing its action', () => {
    fixture.componentRef.setInput('bare', true);
    fixture.componentRef.setInput('actionLabel', 'Tạo ca kiểm thử');
    fixture.detectChanges();
    expect(root.querySelector('[data-slot="card"]')).toBeNull();
    expect(root.querySelector('section')?.classList.contains('state-panel--bare')).toBe(true);
    expect(root.querySelector('h2')?.textContent).toBe('No test cases yet');
    expect(root.querySelector('section')?.className).not.toContain('min-h-');
    const action = vi.fn();
    fixture.componentInstance.action.subscribe(action);
    root.querySelector('button')?.click();
    expect(action).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('actionHref', '/features');
    fixture.detectChanges();
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/features');
  });

  it('retains complete long localized copy in the shrinkable bare section', () => {
    const text = 'Chưa có đích cài đặt nào cho quyền hiện tại. '.repeat(15);
    fixture.componentRef.setInput('bare', true);
    fixture.componentRef.setInput('description', text);
    fixture.detectChanges();
    expect(root.querySelector('.state-panel__description')?.textContent).toBe(text);
    expect(root.querySelector('section')?.classList.contains('min-w-0')).toBe(true);
    expect(root.querySelector('section')?.classList.contains('wrap-anywhere')).toBe(true);
  });
});
