import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { HlmIcon } from './index';

const ACTIVITY =
  'M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2';

describe('HlmIcon fixed status geometry', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HlmIcon],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it.each([
    ['lucideClock', 'M12 6v6l4 2', true],
    ['lucideActivity', ACTIVITY, false],
    ['lucideCircle', null, true],
    ['lucideCopy', 'M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2', false],
    ['lucideMaximize2', 'M15 3h6v6', false],
  ] as const)(
    'renders pinned Lucide geometry for %s without an SVG-string sink',
    (name, path, circle) => {
      const fixture = TestBed.createComponent(HlmIcon);
      fixture.componentRef.setInput('name', name);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const svg = root.querySelector('svg');
      expect(root.getAttribute('aria-hidden')).toBe('true');
      expect(svg?.getAttribute('viewBox')).toBe('0 0 24 24');
      expect(svg?.getAttribute('stroke')).toBe('currentColor');
      expect(svg?.getAttribute('focusable')).toBe('false');
      expect(root.querySelector('path')?.getAttribute('d') ?? null).toBe(path);
      expect(Boolean(root.querySelector('circle'))).toBe(circle);
      expect(root.querySelector('script, foreignObject, animate, [style]')).toBeNull();
      expect(root.classList.contains('shrink-0')).toBe(true);
    },
  );

  it('pins the complete copy and expand shapes', () => {
    const fixture = TestBed.createComponent(HlmIcon);
    fixture.componentRef.setInput('name', 'lucideCopy');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const rectangle = root.querySelector('rect');
    expect(rectangle?.getAttribute('width')).toBe('14');
    expect(rectangle?.getAttribute('height')).toBe('14');
    expect(rectangle?.getAttribute('x')).toBe('8');
    expect(rectangle?.getAttribute('y')).toBe('8');
    expect(rectangle?.getAttribute('rx')).toBe('2');
    fixture.componentRef.setInput('name', 'lucideMaximize2');
    fixture.detectChanges();
    expect(Array.from(root.querySelectorAll('path'), (path) => path.getAttribute('d'))).toEqual([
      'M15 3h6v6',
      'm21 3-7 7',
      'm3 21 7-7',
      'M9 21H3v-6',
    ]);
  });
});
