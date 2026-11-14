import { formatDate, registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { AbsoluteTimeComponent } from './absolute-time.component';

registerLocaleData(localeVi);

const raw = '2026-11-07T15:10:00.123456+07:00';

describe.each(['en-US', 'vi'])('AbsoluteTimeComponent (%s)', (locale) => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AbsoluteTimeComponent],
      providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: locale }],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  it.each(['light', 'dark'])('formats a UTC instant in the active locale in %s theme', (theme) => {
    const fixture = TestBed.createComponent(AbsoluteTimeComponent);
    fixture.componentRef.setInput('value', raw);
    const host = fixture.nativeElement as HTMLElement;
    host.setAttribute('data-theme', theme);
    fixture.detectChanges();
    const time = host.querySelector('time')!;
    const expected = formatDate(Date.UTC(2026, 10, 7, 8, 10, 0, 123), 'medium', locale, 'UTC');
    expect(time.textContent).toBe(`${expected} UTC`);
    expect(time.getAttribute('datetime')).toBe(raw);
    expect(host.getAttribute('data-theme')).toBe(theme);
    fixture.destroy();
  });

  it.each([
    '2026-02-30T00:00:00Z',
    '2026-11-07',
    '2026-11-07T08:10:00',
    'wrong',
    '',
    null,
    undefined,
  ])('refuses malformed or absent instants without inventing a datetime: %j', (value) => {
    const fixture = TestBed.createComponent(AbsoluteTimeComponent);
    fixture.componentRef.setInput('value', value);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('time')).toBeNull();
    expect(host.textContent?.trim()).toBe('Time unavailable');
    fixture.destroy();
  });

  it('updates a recycled value without retaining the previous instant', () => {
    const fixture = TestBed.createComponent(AbsoluteTimeComponent);
    fixture.componentRef.setInput('value', raw);
    fixture.detectChanges();
    fixture.componentRef.setInput('value', 'invalid');
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('time')).toBeNull();
    fixture.destroy();
  });
});
