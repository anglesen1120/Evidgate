import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';

import { NumberValueComponent } from './number-value.component';
import { NumberValueHarness } from './values.harness';

describe.each([
  ['en-US', '1,234,567,890,123,456.123456', '456.123456\u00a0requests'],
  ['vi', '1.234.567.890.123.456,123456', '456,123456\u00a0yêu\u00a0cầu'],
])('NumberValueComponent (%s)', (locale, expected, finalGroup) => {
  let fixture: ComponentFixture<NumberValueComponent>;
  let harness: NumberValueHarness;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NumberValueComponent],
      providers: [provideZonelessChangeDetection(), { provide: LOCALE_ID, useValue: locale }],
    }).compileComponents();
    fixture = TestBed.createComponent(NumberValueComponent);
    fixture.componentRef.setInput('value', '1234567890123456.123456');
    fixture.componentRef.setInput('unit', locale === 'vi' ? 'yêu cầu' : 'requests');
    harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, NumberValueHarness);
  });

  afterEach(() => {
    fixture.destroy();
    TestBed.resetTestingModule();
  });

  it.each(['light', 'dark'])('retains locale grouping and all precision in %s', async (theme) => {
    const host = fixture.nativeElement as HTMLElement;
    host.setAttribute('data-theme', theme);
    const groups = await harness.digitGroups();
    expect(groups).toHaveLength(6);
    expect(groups.slice(0, -1).join('') + groups.at(-1)?.split('\u00a0')[0]).toBe(expected);
    expect(groups.at(-1)).toBe(finalGroup);
    expect(host.querySelectorAll('wbr')).toHaveLength(5);
    expect(getComputedStyle(host).fontVariantNumeric).toBe('tabular-nums');
  });

  it('only permits narrow-layout breaks between groups, never inside digits or units', async () => {
    const groups = await harness.digitGroups();
    const host = fixture.nativeElement as HTMLElement;
    for (const group of host.querySelectorAll<HTMLElement>('.number-group')) {
      expect(getComputedStyle(group).whiteSpace).toBe('nowrap');
      expect(getComputedStyle(group).wordBreak).toBe('normal');
    }
    expect(groups.at(-1)).toBe(finalGroup);
    expect(host.querySelector('wbr:last-child')).toBeNull();
  });

  it('does not attach units to unavailable data or turn it into zero', async () => {
    fixture.componentRef.setInput('value', null);
    expect(await harness.text()).toBe('Number unavailable');
    expect(await harness.digitGroups()).toEqual([]);
  });

  it('shows zero with its unit and allows an omitted unit', async () => {
    fixture.componentRef.setInput('value', 0);
    expect((await harness.digitGroups())[0]?.startsWith('0\u00a0')).toBe(true);
    fixture.componentRef.setInput('unit', '');
    expect(await harness.digitGroups()).toEqual(['0']);
  });
});
