import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';

import { InlineFeedbackComponent } from './inline-feedback.component';

@Component({
  selector: 'app-feedback-projection-host',
  imports: [InlineFeedbackComponent, HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-inline-feedback [title]="title" [announcement]="'off'">
      <p id="feedback-context">{{ context }}</p>
      <button
        feedbackAction
        hlmBtn
        [variant]="'outline'"
        type="button"
        aria-disabled="true"
        (click)="attempts = attempts + 1"
      >
        {{ actionLabel }}
      </button>
    </app-inline-feedback>
  `,
})
class FeedbackProjectionHost {
  readonly title = 'Credential refresh unavailable';
  readonly context = 'Keep the current draft. Read the server state before submitting again.';
  readonly actionLabel = 'Read current state';
  attempts = 0;
}

describe('InlineFeedbackComponent', () => {
  let fixture: ComponentFixture<InlineFeedbackComponent>;
  let root: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [InlineFeedbackComponent, FeedbackProjectionHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(InlineFeedbackComponent);
    fixture.componentRef.setInput('title', 'Read the current state');
    fixture.componentRef.setInput('description', 'The previous request could not be confirmed.');
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  it.each([
    ['info', 'lucideInfo', 'status', 'polite', 'spartan-alert-variant-default'],
    ['warning', 'lucideTriangleAlert', 'status', 'polite', 'spartan-alert-variant-default'],
    ['destructive', 'lucideOctagonX', 'alert', 'assertive', 'spartan-alert-variant-destructive'],
    ['success', 'lucideCircleCheck', 'status', 'polite', 'spartan-alert-variant-default'],
  ] as const)(
    'composes the %s tone with text, a decorative icon and one announcement',
    (tone, icon, role, live, variant) => {
      fixture.componentRef.setInput('tone', tone);
      fixture.detectChanges();
      const alert = root.querySelector('[data-slot="alert"]');
      expect(alert?.getAttribute('data-tone')).toBe(tone);
      expect(alert?.classList.contains('spartan-inline-feedback')).toBe(true);
      expect(alert?.classList.contains(variant)).toBe(true);
      expect(alert?.getAttribute('role')).toBe(role);
      expect(alert?.getAttribute('aria-live')).toBe(live);
      expect(alert?.getAttribute('aria-atomic')).toBe('true');
      expect(root.querySelectorAll('[role="alert"], [role="status"]')).toHaveLength(1);
      expect(root.querySelector('[data-slot="alert-title"]')?.textContent).toBe(
        'Read the current state',
      );
      expect(root.querySelector('[data-slot="alert-description"]')?.textContent).toContain(
        'The previous request could not be confirmed.',
      );
      const renderedIcon = fixture.debugElement.query(By.directive(HlmIcon)).injector.get(HlmIcon);
      expect(renderedIcon.name()).toBe(icon);
      expect(root.querySelector('hlm-icon')?.getAttribute('aria-hidden')).toBe('true');
    },
  );

  it.each(['light', 'dark'])('keeps the same semantic recipe under the %s theme', (theme) => {
    root.setAttribute('data-theme', theme);
    fixture.componentRef.setInput('tone', 'warning');
    fixture.detectChanges();
    expect(
      root.querySelector('[data-tone="warning"]')?.classList.contains('spartan-inline-feedback'),
    ).toBe(true);
    expect(root.querySelector<HTMLElement>('[data-slot="alert"]')?.style.color).toBe('');
    expect(root.querySelector('[data-slot="alert-description"]')?.textContent).toContain(
      'The previous request could not be confirmed.',
    );
  });

  it('offers only the supplied secondary action and guards native disabled activation', () => {
    expect(root.querySelector('button, a')).toBeNull();
    fixture.componentRef.setInput('actionLabel', 'Read current state');
    fixture.detectChanges();
    const action = vi.fn();
    fixture.componentInstance.action.subscribe(action);
    const button = root.querySelector('button')!;
    const renderedButton = fixture.debugElement
      .query(By.directive(HlmButton))
      .injector.get(HlmButton);
    expect(button.type).toBe('button');
    expect(renderedButton.variant()).toBe('outline');
    expect(button.classList.contains('h-auto')).toBe(true);
    expect(button.classList.contains('max-w-full')).toBe(true);
    expect(button.classList.contains('whitespace-normal')).toBe(true);
    button.click();
    expect(action).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('actionDisabled', true);
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    button.click();
    expect(action).toHaveBeenCalledTimes(1);
    fixture.debugElement.query(By.directive(HlmButton)).triggerEventHandler('click');
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('allows a static notice or an explicitly polite error without duplicate live regions', () => {
    fixture.componentRef.setInput('announcement', 'off');
    fixture.detectChanges();
    expect(root.querySelector('[data-slot="alert"]')?.getAttribute('role')).toBe('group');
    expect(root.querySelector('[aria-live], [role="alert"], [role="status"]')).toBeNull();
    fixture.componentRef.setInput('tone', 'destructive');
    fixture.componentRef.setInput('announcement', 'polite');
    fixture.detectChanges();
    expect(root.querySelectorAll('[role="status"]')).toHaveLength(1);
    expect(root.querySelector('[role="alert"]')).toBeNull();
  });

  it('retains complete expanded localized copy and a shrinkable recovery control', () => {
    const text = 'Không thể xác nhận phản hồi. Hãy đọc lại trạng thái trước khi gửi. '.repeat(20);
    const literal = text + '<script>untrusted</script>';
    fixture.componentRef.setInput('title', literal);
    fixture.componentRef.setInput('description', literal);
    fixture.componentRef.setInput('actionLabel', literal);
    fixture.detectChanges();
    expect(root.querySelector('[data-slot="alert-title"]')?.textContent).toBe(literal);
    expect(root.querySelector('[data-slot="alert-description"]')?.textContent).toContain(literal);
    expect(root.querySelector('button')?.textContent?.trim()).toBe(literal);
    expect(root.querySelector('button')?.classList.contains('min-w-0')).toBe(true);
    expect(root.querySelector('script, img, [title]')).toBeNull();
  });

  it('projects rich context and a focus-preserving caller-owned Helm action into their slots', () => {
    const projected = TestBed.createComponent(FeedbackProjectionHost);
    projected.detectChanges();
    const host = projected.nativeElement as HTMLElement;
    const context = host.querySelector('#feedback-context');
    expect(context?.parentElement?.getAttribute('data-slot')).toBe('alert-description');
    const button = host.querySelector('button')!;
    expect(button.parentElement?.getAttribute('data-slot')).toBe('alert-action');
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.disabled).toBe(false);
    expect(host.querySelectorAll('button')).toHaveLength(1);
  });
});
