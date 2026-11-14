import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmButton } from '@evidgate/ui/button';

import { HeaderHarness } from './header.harness';
import { SectionHeaderImports } from './index';

@Component({
  selector: 'app-section-header-test-host',
  imports: [SectionHeaderImports, HlmButton],
  template: `
    <app-section-header
      [title]="title()"
      [description]="description()"
      [headingLevel]="level()"
      [size]="size()"
      [titleId]="titleId()"
    >
      @if (showAction()) {
        <button
          hlmBtn
          appSectionHeaderActions
          [variant]="actionVariant"
          type="button"
          (click)="calls = calls + 1"
        >
          {{ actionLabel }}
        </button>
      }
    </app-section-header>
  `,
})
class SectionHeaderTestHost {
  readonly actionLabel = 'Inspect all attempts';
  readonly title = signal('Attempts');
  readonly description = signal<string | undefined>('Recorded execution attempts.');
  readonly titleId = signal<string | undefined>('attempts-title');
  readonly level = signal<2 | 3>(2);
  readonly size = signal<'section' | 'subsection'>('section');
  readonly showAction = signal(true);
  readonly actionVariant = 'outline' as const;
  calls = 0;
}

describe('SectionHeaderComponent', () => {
  let fixture: ComponentFixture<SectionHeaderTestHost>;
  let harness: HeaderHarness;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SectionHeaderTestHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(SectionHeaderTestHost);
    fixture.detectChanges();
    harness = await TestbedHarnessEnvironment.loader(fixture).getHarness(HeaderHarness);
  });

  it('defaults to a section-role h2 without a page h1', async () => {
    expect(await harness.getHeadingLevel()).toBe('H2');
    expect(await harness.getHeadingCount()).toBe(1);
    expect(await harness.getCardCount()).toBe(0);
    expect(await harness.getSize()).toBe('section');
    expect(await harness.getTitle()).toBe('Attempts');
    expect(await harness.getTitleId()).toBe('attempts-title');
    expect(await harness.getDescription()).toBe('Recorded execution attempts.');
  });

  it.each([
    [2, 'section'],
    [2, 'subsection'],
    [3, 'section'],
    [3, 'subsection'],
  ] as const)('keeps h%s semantics independent of the %s visual role', async (level, size) => {
    fixture.componentInstance.level.set(level);
    fixture.componentInstance.size.set(size);
    expect(await harness.getHeadingLevel()).toBe(`H${level}`);
    expect(await harness.getSize()).toBe(size);
    expect(await harness.getTitleId()).toBe('attempts-title');
  });

  it('projects a wrapping local action after the title and preserves its command', async () => {
    const content = await harness.getContent();
    expect(content.indexOf('Attempts')).toBeLessThan(content.indexOf('Inspect all attempts'));
    expect(await harness.isActionWrappingEnabled()).toBe(true);
    await harness.focusAction();
    expect(await harness.isActionFocused()).toBe(true);
    await harness.clickAction();
    expect(fixture.componentInstance.calls).toBe(1);
  });

  it('omits absent optional copy, actions and id without adding a decorative card', async () => {
    fixture.componentInstance.description.set(undefined);
    fixture.componentInstance.showAction.set(false);
    fixture.componentInstance.titleId.set(undefined);
    expect(await harness.getDescription()).toBeNull();
    expect(await harness.hasAction()).toBe(false);
    expect(await harness.getTitleId()).toBeNull();
    expect(await harness.getContent()).toBe('Attempts');
  });

  it('keeps complete localized section and card copy when content grows', async () => {
    const title = 'Nguyễn — Quyền kiểm thử <img src=x> '.repeat(12).trim();
    fixture.componentInstance.title.set(title);
    fixture.componentInstance.level.set(3);
    fixture.componentInstance.size.set('subsection');
    expect(await harness.getTitle()).toBe(title);
    expect(await harness.getHeadingLevel()).toBe('H3');
    expect(await harness.getSize()).toBe('subsection');
  });
});
