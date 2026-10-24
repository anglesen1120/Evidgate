// 09.1.1-T4.2: URL-owned scope; docs/design/web/state-sheet.md#shared-accessibility-and-themes.
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ScopeSwitcherComponent } from './scope-switcher.component';
import { SCOPE_ORGANIZATIONS, SCOPE_PROJECTS } from './scope.fixture';

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
});
