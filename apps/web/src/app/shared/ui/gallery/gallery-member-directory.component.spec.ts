import { BreakpointObserver } from '@angular/cdk/layout';
import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { provideGalleryLayout } from './gallery-layout.testing';
import { GalleryMemberDirectoryComponent } from './gallery-member-directory.component';

class RoleFilterOptions extends ComponentHarness {
  static hostSelector = '[role="listbox"]';
  readonly options = this.locatorForAll('[role="option"]');
}

class DirectoryHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-member-directory';
  readonly caption = this.locatorFor('caption');
  readonly columns = this.locatorForAll('th[scope="col"]');
  readonly view = this.locatorFor('tbody tr:first-child [data-action="view"]');
  readonly edit = this.locatorFor('tbody tr:first-child [data-action="edit"]');
  readonly edits = this.locatorForAll('[data-action="edit"]');
  readonly firstRow = this.locatorFor('tbody tr:first-child');
  readonly range = this.locatorFor('[data-range]');
  readonly next = this.locatorFor('footer [data-next]');
  readonly pending = this.locatorFor('[data-filter="pending"]');
  readonly search = this.locatorFor('input[type="search"]');
  readonly roleFilter = this.locatorFor('#gallery-role-filter');
  readonly metrics = this.locatorForAll('dl.metrics dd:not(.context)');
  readonly context = this.locatorFor('app-section-header p');

  async enterSearch(value: string): Promise<void> {
    const input = await this.search();
    await input.setInputValue(value);
    await input.dispatchEvent('input');
  }
}

class MemberDialogHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly title = this.locatorFor('h2');
  readonly facts = this.locatorFor('dl');
  readonly status = this.locatorFor('header app-status-pill');
  readonly reviewer = this.locatorFor('[role="checkbox"][aria-label="Reviewer"]');
  readonly save = this.locatorFor('[data-save]');
  readonly cancel = this.locatorFor('[data-cancel]');
  readonly keep = this.locatorFor('[data-keep]');
  readonly discard = this.locatorFor('[data-discard]');
}

class BackdropHarness extends ComponentHarness {
  static hostSelector = '.cdk-overlay-backdrop';
}

async function mount(desktop = true) {
  TestBed.configureTestingModule({
    providers: [
      {
        provide: BreakpointObserver,
        useValue: {
          isMatched: () => desktop,
          observe: () => of({ matches: desktop, breakpoints: {} }),
        },
      },
    ],
  });
  const fixture = TestBed.createComponent(GalleryMemberDirectoryComponent);
  const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
  const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, DirectoryHarness);
  return { table, root };
}

describe('Member directory specimen', () => {
  provideGalleryLayout();

  it('keeps View available but does not offer Edit on narrow screens', async () => {
    const { table } = await mount(false);
    expect(await table.edits()).toHaveLength(0);
    expect(await (await table.view()).getAttribute('aria-label')).toBe('View linh@example.test');
  });

  it('filters and pages only the sample records and keeps the displayed range truthful', async () => {
    const { table } = await mount();
    expect(await (await table.context()).text()).toBe('Members: 3 · Pending invitations: 1');
    expect(await Promise.all((await table.metrics()).map((value) => value.text()))).toEqual([
      '3',
      '2',
      '1',
    ]);
    expect(await (await table.caption()).text()).toBe('Sample members');
    expect(await Promise.all((await table.columns()).map((column) => column.text()))).toEqual([
      'Member email',
      'Status',
      'Roles',
      'Projects',
      'Actions',
    ]);
    expect(await (await table.range()).text()).toBe('Showing 1–2 of 3');
    await (await table.next()).click();
    expect(await (await table.range()).text()).toBe('Showing 3–3 of 3');
    await (await table.pending()).click();
    expect(await (await table.range()).text()).toBe('Showing 1–1 of 1');
    expect(await (await table.firstRow()).text()).toContain('Invite pending');
  });

  it('resets paging on search, exposes an empty result, and recovers when cleared', async () => {
    const { table } = await mount();
    await (await table.next()).click();
    await table.enterSearch('LINH@');
    expect(await (await table.range()).text()).toBe('Showing 1–1 of 1');
    expect(await (await table.firstRow()).text()).toContain('linh@example.test');
    await table.enterSearch('nobody@example.test');
    expect(await (await table.firstRow()).text()).toContain('No sample members match');
    await table.enterSearch('');
    expect(await (await table.range()).text()).toBe('Showing 1–2 of 3');
  });

  it('returns focus to search when removing a role removes the opening row', async () => {
    const { table, root } = await mount();
    await (await table.roleFilter()).click();
    const roles = await root.getHarness(RoleFilterOptions);
    for (const option of await roles.options()) {
      if ((await option.text()) === 'Reviewer') {
        await option.click();
        break;
      }
    }
    expect(await (await table.range()).text()).toBe('Showing 1–2 of 2');
    await (await table.edit()).click();
    const dialog = await root.getHarness(MemberDialogHarness);
    await (await dialog.reviewer()).click();
    await (await dialog.save()).click();
    expect(await (await table.range()).text()).toBe('Showing 1–1 of 1');
    expect(await (await table.search()).isFocused()).toBe(true);
  });

  it('opens a titled View for the selected email and returns focus to that row on Escape', async () => {
    const { table, root } = await mount();
    const trigger = await table.view();
    expect(await trigger.getAttribute('aria-label')).toBe('View linh@example.test');
    await trigger.click();
    const dialog = await root.getHarness(MemberDialogHarness);
    const title = await dialog.title();
    expect(await (await dialog.host()).getAttribute('aria-labelledby')).toBe(
      await title.getAttribute('id'),
    );
    expect(await title.text()).toBe('Member details');
    expect(await (await dialog.facts()).text()).toContain('linh@example.test');
    expect(await (await dialog.status()).text()).toContain('Active');
    await title.sendKeys(TestKey.ESCAPE);
    expect(await root.hasHarness(MemberDialogHarness)).toBe(false);
    expect(await trigger.isFocused()).toBe(true);
  });

  it('guards backdrop dismissal and lets a reverted draft close without a prompt', async () => {
    const { table, root } = await mount();
    const opener = await table.edit();
    await opener.click();
    const dialog = await root.getHarness(MemberDialogHarness);
    await (await dialog.reviewer()).click();
    const backdrop = await root.getHarness(BackdropHarness);
    await (await backdrop.host()).click();
    expect(await (await dialog.title()).text()).toBe('Discard unsaved changes?');
    await (await backdrop.host()).click();
    expect(await (await dialog.title()).text()).toBe('Discard unsaved changes?');
    await (await dialog.keep()).click();
    await (await dialog.reviewer()).click();
    expect(await (await dialog.save()).getProperty('disabled')).toBe(true);
    await (await dialog.cancel()).click();
    expect(await root.hasHarness(MemberDialogHarness)).toBe(false);
    expect(await opener.isFocused()).toBe(true);
  });

  it('keeps draft changes on Keep editing, discards without changing the row, and restores its Edit button', async () => {
    const { table, root } = await mount();
    const opener = await table.edit();
    await opener.click();
    const dialog = await root.getHarness(MemberDialogHarness);
    const title = await dialog.title();
    expect(await (await dialog.host()).getAttribute('aria-labelledby')).toBe(
      await title.getAttribute('id'),
    );
    expect(await title.text()).toBe('Edit organization roles');
    expect(await (await dialog.save()).getProperty('disabled')).toBe(true);
    await (await dialog.reviewer()).click();
    expect(await (await dialog.save()).getProperty('disabled')).toBe(false);
    await (await dialog.reviewer()).sendKeys(TestKey.ESCAPE);
    expect(await (await dialog.keep()).isFocused()).toBe(true);
    await (await dialog.keep()).click();
    expect(await (await dialog.reviewer()).getAttribute('aria-checked')).toBe('true');
    await (await dialog.cancel()).click();
    await (await dialog.discard()).click();
    expect(await root.hasHarness(MemberDialogHarness)).toBe(false);
    expect(await opener.isFocused()).toBe(true);
    expect(await (await table.firstRow()).text()).not.toContain('Reviewer');
    await opener.click();
    const reopened = await root.getHarness(MemberDialogHarness);
    await (await reopened.reviewer()).click();
    await (await reopened.save()).click();
    expect(await root.hasHarness(MemberDialogHarness)).toBe(false);
    expect(await (await table.firstRow()).text()).toContain('Reviewer');
  });
});
