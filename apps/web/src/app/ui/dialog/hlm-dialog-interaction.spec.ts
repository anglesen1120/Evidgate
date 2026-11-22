import { InteractivityChecker } from '@angular/cdk/a11y';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ChangeDetectionStrategy, Component, signal, TemplateRef, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HlmDialogImports, HlmDialogService } from './index';

@Component({
  selector: 'app-record-dialog-host',
  imports: [HlmDialogImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog>
      <button hlmDialogTrigger>{{ title }}</button>
      <hlm-dialog-content *hlmDialogPortal [size]="size()" [closeGuard]="canClose">
        @if (editing()) {
          <header hlmDialogHeader>
            <span hlmDialogStatusIcon aria-hidden="true"></span>
            <h2 hlmDialogTitle>{{ title }}</h2>
            <p hlmDialogSubject>{{ subject }}</p>
          </header>
          <label
            ><span>{{ subject }}</span
            ><input
          /></label>
          <footer hlmDialogFooter>
            <button hlmDialogClose>{{ cancel }}</button>
          </footer>
        }
      </hlm-dialog-content>
    </hlm-dialog>
    <ng-template #content
      ><h2 hlmDialogTitle>{{ title }}</h2></ng-template
    >
  `,
})
class RecordDialogHost {
  readonly title = 'Edit roles';
  readonly subject = 'Current member';
  readonly cancel = 'Cancel';
  readonly editing = signal(true);
  readonly size = signal<'lg' | undefined>('lg');
  readonly dirty = signal(false);
  readonly canClose = vi.fn(() => !this.dirty());
  readonly content = viewChild.required<TemplateRef<unknown>>('content');
}

class RecordDialogHarness extends ComponentHarness {
  static hostSelector = 'app-record-dialog-host';
  readonly opener = this.locatorFor('[hlmDialogTrigger]');
  readonly modal = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  readonly content = this.documentRootLocatorFactory().locatorFor('hlm-dialog-content');
  readonly title = this.documentRootLocatorFactory().locatorFor('hlm-dialog-content > header h2');
  readonly input = this.documentRootLocatorFactory().locatorFor('hlm-dialog-content > label input');
  readonly cancel = this.documentRootLocatorFactory().locatorFor(
    'hlm-dialog-content > footer button',
  );
  readonly close = this.documentRootLocatorFactory().locatorFor('.spartan-dialog-close');
  readonly backdrop = this.documentRootLocatorFactory().locatorFor('.cdk-overlay-backdrop');
  readonly anchors = this.documentRootLocatorFactory().locatorForAll('.cdk-focus-trap-anchor');

  async open() {
    const opener = await this.opener();
    await opener.focus();
    await opener.click();
    return opener;
  }

  async escape() {
    await (await this.input()).sendKeys(TestKey.ESCAPE);
  }
}

async function setup() {
  vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  const fixture = TestBed.createComponent(RecordDialogHost);
  const ui = await TestbedHarnessEnvironment.harnessForFixture(fixture, RecordDialogHarness);
  return { fixture, ui };
}

describe('record dialog accessibility', () => {
  afterEach(() => vi.restoreAllMocks());

  it('keeps conditional header, fields and footer as direct children without a size', async () => {
    const { fixture, ui } = await setup();
    fixture.componentInstance.size.set(undefined);
    await ui.open();
    expect(await (await ui.content()).getAttribute('data-size')).toBeNull();
    expect(await (await ui.title()).text()).toBe(fixture.componentInstance.title);
    expect(await (await ui.input()).isFocused()).toBe(true);
    await (await ui.cancel()).click();
    expect(await ui.modal()).toBeNull();
  });

  it('labels the modal with its heading and keeps focus in one trap until Escape returns it', async () => {
    const { ui } = await setup();
    const opener = await ui.open();
    const titleId = await (await ui.title()).getAttribute('id');
    expect(titleId).toBeTruthy();
    expect(await (await ui.modal())?.getAttribute('aria-labelledby')).toBe(titleId);
    expect(await (await ui.content()).hasClass('spartan-dialog-lg')).toBe(true);
    expect(await (await ui.input()).isFocused()).toBe(true);
    const anchors = await ui.anchors();
    expect(anchors).toHaveLength(2);
    for (const anchor of anchors) {
      await anchor.focus();
      const content = TestbedHarnessEnvironment.getNativeElement(await ui.content());
      expect(content.contains(document.activeElement)).toBe(true);
    }
    await ui.escape();
    expect(await ui.modal()).toBeNull();
    expect(await opener.isFocused()).toBe(true);
  });

  it.each(['escape', 'close', 'cancel', 'backdrop'] as const)(
    'keeps the draft on %s, then permits the same action after release',
    async (exit) => {
      const { fixture, ui } = await setup();
      fixture.componentInstance.dirty.set(true);
      const opener = await ui.open();
      const dismiss = async () => {
        if (exit === 'escape') await ui.escape();
        else await (await ui[exit]()).click();
      };
      await dismiss();
      expect(fixture.componentInstance.canClose).toHaveBeenCalledTimes(1);
      expect(await (await ui.content()).getAttribute('data-state')).toBe('open');
      fixture.componentInstance.dirty.set(false);
      await dismiss();
      expect(fixture.componentInstance.canClose).toHaveBeenCalledTimes(2);
      expect(await ui.modal()).toBeNull();
      expect(await opener.isFocused()).toBe(true);
    },
  );

  it('applies service sizing and guards even a close requested before the first render', async () => {
    const { fixture, ui } = await setup();
    const host = fixture.componentInstance;
    host.dirty.set(true);
    const ref = TestBed.inject(HlmDialogService).open<string>(host.content(), {
      size: 'md',
      closeGuard: host.canClose,
    });
    const closed = vi.fn();
    ref.closed$.subscribe(closed);
    ref.close('blocked');
    expect(ref.open).toBe(true);
    expect(await (await ui.content()).hasClass('spartan-dialog-md')).toBe(true);
    host.dirty.set(false);
    ref.close('permitted');
    await fixture.whenStable();
    expect(closed).toHaveBeenCalledWith('permitted');
    expect(await ui.modal()).toBeNull();
  });
});
