import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ScopeSwitcherComponent } from './scope-switcher.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS, stubScopeBrowserApis } from './scope.fixture';

class ScopeHarness extends ComponentHarness {
  static hostSelector = 'app-scope-switcher';
  readonly triggers = this.locatorForAll('button[role="combobox"]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly listbox = this.documentRootLocatorFactory().locatorFor('[role="listbox"]');

  async trigger(index: number) {
    const trigger = (await this.triggers())[index];
    if (!trigger) throw new Error(`Missing scope trigger ${index}`);
    return trigger;
  }

  async choose(index: number, name: string): Promise<void> {
    await (await this.trigger(index)).click();
    for (const option of await this.options()) {
      if ((await option.text()).trim() === name) {
        await option.click();
        return;
      }
    }
    throw new Error(`Missing scope option ${name}`);
  }
}

async function createFixture() {
  const fixture = TestBed.createComponent(ScopeSwitcherComponent);
  fixture.componentRef.setInput('organizations', SCOPE_ORGANIZATIONS);
  fixture.componentRef.setInput('projects', SCOPE_PROJECTS);
  fixture.componentRef.setInput('currentOrg', SCOPE_ORGANIZATIONS[0]);
  fixture.componentRef.setInput('currentProject', SCOPE_PROJECTS[0]);
  await fixture.whenStable();
  const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ScopeHarness);
  return { fixture, harness, root: fixture.nativeElement as HTMLElement };
}

describe('ScopeSwitcherComponent', () => {
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
    return TestBed.configureTestingModule({
      imports: [ScopeSwitcherComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  it('labels both Brain comboboxes and reflects URL-owned scope', async () => {
    const { fixture, harness, root } = await createFixture();
    expect(await (await harness.trigger(0)).text()).toContain(SCOPE_ORGANIZATIONS[0].name);
    expect(await (await harness.trigger(1)).text()).toContain(SCOPE_PROJECTS[0].name);
    expect(root.querySelector(`label[for="${fixture.componentInstance.orgId}"]`)?.textContent).toBe(
      'Organization',
    );
    expect(
      root.querySelector(`label[for="${fixture.componentInstance.projectId}"]`)?.textContent,
    ).toBe('Project');
    expect(await (await harness.trigger(0)).getAttribute('aria-haspopup')).toBe('listbox');
    expect(await (await harness.trigger(0)).getAttribute('aria-expanded')).toBe('false');
  });

  it('emits activated server ids without mutating current scope', async () => {
    const { fixture, harness } = await createFixture();
    const orgSelected = vi.fn();
    const projectSelected = vi.fn();
    fixture.componentInstance.orgSelected.subscribe(orgSelected);
    fixture.componentInstance.projectSelected.subscribe(projectSelected);
    await harness.choose(0, SCOPE_ORGANIZATIONS[1].name);
    await harness.choose(1, SCOPE_PROJECTS[1].name);
    expect(orgSelected).toHaveBeenCalledWith('org-two');
    expect(projectSelected).toHaveBeenCalledWith('project-two');
    expect(fixture.componentInstance.currentProject()?.id).toBe('project-one');
  });

  it('disables absent scopes and names the empty project state', async () => {
    const fixture = TestBed.createComponent(ScopeSwitcherComponent);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('[role="combobox"]'));
    expect(buttons).toHaveLength(2);
    expect(buttons.every((button) => button.disabled)).toBe(true);
    expect(root.textContent).toContain('No projects');
  });

  it('keeps complete long Vietnamese option names in the popup', async () => {
    const { harness } = await createFixture();
    await (await harness.trigger(0)).click();
    const names = await Promise.all((await harness.options()).map((option) => option.text()));
    expect(names.map((name) => name.trim())).toContain(SCOPE_ORGANIZATIONS[1].name);
  });

  it.each([0, 1])('applies animate-none to scope popup %i', async (index) => {
    const { harness } = await createFixture();
    await (await harness.trigger(index)).click();
    expect(document.querySelector('hlm-select-content')?.classList.contains('animate-none')).toBe(
      true,
    );
  });

  it('explores options with arrows and commits only on Enter', async () => {
    const { fixture, harness } = await createFixture();
    const selected = vi.fn();
    fixture.componentInstance.orgSelected.subscribe(selected);
    const trigger = await harness.trigger(0);
    await trigger.sendKeys(TestKey.ENTER);
    expect(await trigger.getAttribute('aria-expanded')).toBe('true');
    expect(await (await harness.listbox()).getAttribute('aria-labelledby')).toMatch(
      /^scope-org-\d+-label$/,
    );
    await trigger.sendKeys(TestKey.END);
    expect(selected).not.toHaveBeenCalled();
    await trigger.sendKeys(TestKey.ENTER);
    expect(selected).toHaveBeenCalledExactlyOnceWith('org-two');
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('dismisses a keyboard exploration with Tab without navigating', async () => {
    const { fixture, harness } = await createFixture();
    const selected = vi.fn();
    fixture.componentInstance.projectSelected.subscribe(selected);
    const trigger = await harness.trigger(1);
    await trigger.click();
    await trigger.sendKeys(TestKey.END);
    await trigger.sendKeys(TestKey.TAB);
    expect(selected).not.toHaveBeenCalled();
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('stops claiming the Escape of a scope whose select became disabled', async () => {
    const { fixture, harness } = await createFixture();
    const escapes = vi.fn();
    fixture.componentInstance.popupEscape.subscribe(escapes);
    await (await harness.trigger(1)).click();
    expect(await (await harness.trigger(1)).getAttribute('aria-expanded')).toBe('true');

    // Emptying the project list disables the select whose popup was open: it can no longer report
    // that close, so the entry must not swallow the Escape that now belongs to the drawer.
    fixture.componentRef.setInput('projects', []);
    await fixture.whenStable();
    expect(await (await harness.trigger(1)).getProperty<boolean>('disabled')).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(escapes).not.toHaveBeenCalled();

    // Brain keeps that popup mounted with no disabled-to-close path, so this Escape closes the
    // popup and the drawer together. Accepted: a disabled select can never reopen one, and a
    // retained entry would keep the drawer closed to Escape for as long as the page lives.
    expect(await (await harness.trigger(1)).getAttribute('aria-expanded')).toBe('true');
  });

  it('retains accessible labels in compact mode and ignores the current selection', async () => {
    const { fixture, harness, root } = await createFixture();
    fixture.componentRef.setInput('compact', true);
    const selected = vi.fn();
    fixture.componentInstance.orgSelected.subscribe(selected);
    await harness.choose(0, SCOPE_ORGANIZATIONS[0].name);
    expect(root.getAttribute('data-compact')).toBe('true');
    expect(root.querySelectorAll('label')).toHaveLength(2);
    expect(selected).not.toHaveBeenCalled();
  });
});
