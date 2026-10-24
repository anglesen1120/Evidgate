// 09.1.1-T4.2: URL-owned scope; docs/design/web/state-sheet.md#shared-accessibility-and-themes.
import { provideZonelessChangeDetection } from '@angular/core';
import type { ComponentFixture } from '@angular/core/testing';
import { TestBed } from '@angular/core/testing';
import { ScopeSwitcherComponent } from './scope-switcher.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS } from './scope.fixture';

/** A scope control ready with both lists and the first entry of each selected. */
async function createFixture(): Promise<{
  fixture: ComponentFixture<ScopeSwitcherComponent>;
  orgSelect: HTMLSelectElement;
  projectSelect: HTMLSelectElement;
}> {
  const fixture = TestBed.createComponent(ScopeSwitcherComponent);
  fixture.componentRef.setInput('organizations', SCOPE_ORGANIZATIONS);
  fixture.componentRef.setInput('projects', SCOPE_PROJECTS);
  fixture.componentRef.setInput('currentOrg', SCOPE_ORGANIZATIONS[0]);
  fixture.componentRef.setInput('currentProject', SCOPE_PROJECTS[0]);
  await fixture.whenStable();
  const selects = (fixture.nativeElement as HTMLElement).querySelectorAll('select');
  return {
    fixture,
    orgSelect: selects[0]!,
    projectSelect: selects[1]!,
  };
}

const press = (element: Element, key: string): void => {
  element.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
};

/** What a stepping key plus the browser's own `change` looks like to the component. */
const stepTo = (select: HTMLSelectElement, key: string, value: string): void => {
  press(select, key);
  select.value = value;
  select.dispatchEvent(new Event('change'));
};

describe('ScopeSwitcherComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [ScopeSwitcherComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );

  it('reflects the current scope and emits server-supplied ids without mutating inputs', async () => {
    const fixture = TestBed.createComponent(ScopeSwitcherComponent);
    fixture.componentRef.setInput('organizations', SCOPE_ORGANIZATIONS);
    fixture.componentRef.setInput('projects', SCOPE_PROJECTS);
    fixture.componentRef.setInput('currentOrg', SCOPE_ORGANIZATIONS[0]);
    fixture.componentRef.setInput('currentProject', SCOPE_PROJECTS[0]);
    const orgSelected = vi.fn();
    const projectSelected = vi.fn();
    fixture.componentInstance.orgSelected.subscribe(orgSelected);
    fixture.componentInstance.projectSelected.subscribe(projectSelected);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const orgSelect = root.querySelectorAll('select')[0]!;
    const projectSelect = root.querySelectorAll('select')[1]!;
    expect(orgSelect.value).toBe('org-one');
    expect(projectSelect.value).toBe('project-one');
    expect(root.querySelectorAll('label')).toHaveLength(2);
    orgSelect.value = 'org-two';
    orgSelect.dispatchEvent(new Event('change'));
    projectSelect.value = 'project-two';
    projectSelect.dispatchEvent(new Event('change'));
    expect(orgSelected).toHaveBeenCalledWith('org-two');
    expect(projectSelected).toHaveBeenCalledWith('project-two');
    expect(fixture.componentInstance.currentProject()?.id).toBe('project-one');
  });

  it('disables absent scopes and names the empty project state', async () => {
    const fixture = TestBed.createComponent(ScopeSwitcherComponent);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(Array.from(root.querySelectorAll('select')).every((select) => select.disabled)).toBe(
      true,
    );
    expect(root.textContent).toContain('No projects');
  });

  it('keeps complete long Vietnamese option names in the native controls', async () => {
    const fixture = TestBed.createComponent(ScopeSwitcherComponent);
    fixture.componentRef.setInput('organizations', SCOPE_ORGANIZATIONS);
    fixture.componentRef.setInput('projects', SCOPE_PROJECTS);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain(SCOPE_ORGANIZATIONS[1].name);
    expect(root.textContent).toContain(SCOPE_PROJECTS[1].name);
  });

  it('holds an arrow-key step until Enter instead of emitting on each step', async () => {
    const { fixture, orgSelect } = await createFixture();
    const orgSelected = vi.fn();
    fixture.componentInstance.orgSelected.subscribe(orgSelected);

    stepTo(orgSelect, 'ArrowDown', 'org-two');
    expect(orgSelected).not.toHaveBeenCalled();

    press(orgSelect, 'Enter');
    expect(orgSelected).toHaveBeenCalledTimes(1);
    expect(orgSelected).toHaveBeenCalledWith('org-two');
  });

  it('holds a step across several keys and commits the last value on blur', async () => {
    const { fixture, projectSelect } = await createFixture();
    const projectSelected = vi.fn();
    fixture.componentInstance.projectSelected.subscribe(projectSelected);

    stepTo(projectSelect, 'ArrowDown', 'project-two');
    // Stepping back onto the current project is still only a step: nothing is emitted.
    stepTo(projectSelect, 'ArrowUp', 'project-one');
    expect(projectSelected).not.toHaveBeenCalled();

    stepTo(projectSelect, 'End', 'project-two');
    projectSelect.dispatchEvent(new Event('blur'));
    expect(projectSelected).toHaveBeenCalledTimes(1);
    expect(projectSelected).toHaveBeenCalledWith('project-two');
  });

  it('commits a stepped value when Tab moves focus away (keydown, then blur)', async () => {
    const { fixture, projectSelect } = await createFixture();
    const projectSelected = vi.fn();
    fixture.componentInstance.projectSelected.subscribe(projectSelected);

    stepTo(projectSelect, 'ArrowDown', 'project-two');
    expect(projectSelected).not.toHaveBeenCalled();

    // Tab must leave the step open: the commit happens in the blur that follows it.
    press(projectSelect, 'Tab');
    expect(projectSelected).not.toHaveBeenCalled();
    projectSelect.dispatchEvent(new Event('blur'));
    expect(projectSelected).toHaveBeenCalledTimes(1);
    expect(projectSelected).toHaveBeenCalledWith('project-two');
  });

  it('commits a pointer pick at once, even after a keyboard step', async () => {
    const { fixture, projectSelect } = await createFixture();
    const projectSelected = vi.fn();
    fixture.componentInstance.projectSelected.subscribe(projectSelected);

    stepTo(projectSelect, 'ArrowDown', 'project-two');
    projectSelect.dispatchEvent(new Event('pointerdown'));
    projectSelect.dispatchEvent(new Event('change'));
    expect(projectSelected).toHaveBeenCalledTimes(1);
    expect(projectSelected).toHaveBeenCalledWith('project-two');
  });

  it('commits a typeahead change at once, without waiting for Enter or blur', async () => {
    const { fixture, projectSelect } = await createFixture();
    const projectSelected = vi.fn();
    fixture.componentInstance.projectSelected.subscribe(projectSelected);

    press(projectSelect, 'c');
    projectSelect.value = 'project-two';
    projectSelect.dispatchEvent(new Event('change'));
    expect(projectSelected).toHaveBeenCalledWith('project-two');
  });

  it('emits nothing for a change back to the scope already in the URL', async () => {
    const { fixture, orgSelect } = await createFixture();
    const orgSelected = vi.fn();
    fixture.componentInstance.orgSelected.subscribe(orgSelected);

    orgSelect.value = 'org-one';
    orgSelect.dispatchEvent(new Event('change'));
    expect(orgSelected).not.toHaveBeenCalled();
  });
});
