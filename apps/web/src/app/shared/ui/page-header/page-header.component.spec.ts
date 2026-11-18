import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component, provideZonelessChangeDetection, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmButton } from '@evidgate/ui/button';

import { HeaderHarness } from './header.harness';
import { PageHeaderComponent, PageHeaderImports } from './index';

@Component({
  selector: 'app-page-header-test-host',
  imports: [PageHeaderImports, HlmButton],
  template: `
    <app-page-header [title]="title()" [description]="description()" [titleId]="titleId()">
      @if (showContext()) {
        <nav pageHeaderContext [attr.aria-label]="breadcrumbLabel">{{ breadcrumb }}</nav>
      }
      @if (showAction()) {
        <button
          hlmBtn
          appPageHeaderActions
          type="button"
          [disabled]="busy()"
          (click)="calls = calls + 1"
        >
          {{ actionLabel() }}
        </button>
      }
    </app-page-header>
  `,
})
class PageHeaderTestHost {
  readonly header = viewChild.required(PageHeaderComponent);
  readonly breadcrumbLabel = 'Breadcrumb';
  readonly breadcrumb = 'Organization / Project';
  readonly title = signal('Runs');
  readonly description = signal<string | undefined>('Inspect recorded runs.');
  readonly titleId = signal<string | undefined>('runs-title');
  readonly actionLabel = signal('Create run');
  readonly showContext = signal(true);
  readonly showAction = signal(true);
  readonly busy = signal(false);
  calls = 0;
}

describe('PageHeaderComponent', () => {
  let fixture: ComponentFixture<PageHeaderTestHost>;
  let harness: HeaderHarness;
  let originalTheme: string | null;

  beforeEach(async () => {
    originalTheme = document.documentElement.getAttribute('data-theme');
    await TestBed.configureTestingModule({
      imports: [PageHeaderTestHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(PageHeaderTestHost);
    fixture.detectChanges();
    harness = await TestbedHarnessEnvironment.loader(fixture).getHarness(HeaderHarness);
  });

  afterEach(() => {
    if (originalTheme === null) document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', originalTheme);
  });

  it('renders a single page-role h1 with the existing title id and description', async () => {
    expect(await harness.getHeadingLevel()).toBe('H1');
    expect(await harness.getHeadingCount()).toBe(1);
    expect(await harness.getSize()).toBe('page');
    expect(await harness.getTitle()).toBe('Runs');
    expect(await harness.getTitleId()).toBe('runs-title');
    expect(await harness.getDescription()).toBe('Inspect recorded runs.');
  });

  it('offers the page heading as a programmatic focus destination outside the tab order', () => {
    fixture.componentInstance.header().focusTitle();
    expect(document.activeElement?.id).toBe('runs-title');
    expect(document.activeElement?.getAttribute('tabindex')).toBe('-1');
  });

  it('keeps context, title, description and action in reading and focus order', async () => {
    const content = await harness.getContent();
    expect(content.indexOf('Organization / Project')).toBeLessThan(content.indexOf('Runs'));
    expect(content.indexOf('Runs')).toBeLessThan(content.indexOf('Inspect recorded runs.'));
    expect(content.indexOf('Inspect recorded runs.')).toBeLessThan(content.indexOf('Create run'));
    await harness.focusAction();
    expect(await harness.isActionFocused()).toBe(true);
    await harness.clickAction();
    expect(fixture.componentInstance.calls).toBe(1);
    expect(await harness.isActionWrappingEnabled()).toBe(true);
  });

  it('omits optional content without inventing an action, description or identifier', async () => {
    fixture.componentInstance.showContext.set(false);
    fixture.componentInstance.showAction.set(false);
    fixture.componentInstance.description.set(undefined);
    fixture.componentInstance.titleId.set(undefined);
    expect(await harness.getDescription()).toBeNull();
    expect(await harness.hasAction()).toBe(false);
    expect(await harness.getTitleId()).toBeNull();
    expect(await harness.getContent()).toBe('Runs');
  });

  it('preserves the caller-owned disabled state', async () => {
    fixture.componentInstance.busy.set(true);
    expect(await harness.isActionDisabled()).toBe(true);
    expect(fixture.componentInstance.calls).toBe(0);
  });

  it.each(['light', 'dark'])(
    'retains complete long localized text and action at a constrained host in %s',
    async (theme) => {
      document.documentElement.setAttribute('data-theme', theme);
      (fixture.nativeElement as HTMLElement).style.inlineSize = '320px';
      const title = 'Nguyễn — Quyền kiểm thử <img src=x> '.repeat(10).trim();
      const description = 'Kiểm tra bằng chứng và quyền của bộ kiểm thử. '.repeat(12).trim();
      const action = 'Tạo lượt chạy kiểm thử cho dự án quốc tế';
      fixture.componentInstance.title.set(title);
      fixture.componentInstance.description.set(description);
      fixture.componentInstance.actionLabel.set(action);
      expect(await harness.getTitle()).toBe(title);
      expect(await harness.getDescription()).toBe(description);
      expect(await harness.getContent()).toContain(action);
      expect(await harness.getSize()).toBe('page');
      expect(await harness.isActionWrappingEnabled()).toBe(true);
    },
  );
});
