import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { NotFoundPage } from '../pages/not-found.page';
import { NavComponent } from './nav.component';
import type { NavGroupView } from '../nav/nav.models';

const groups: readonly NavGroupView[] = [
  {
    id: 'design',
    label: 'Test design',
    items: [{ id: 'overview', label: 'Overview', icon: 'home', link: ['/overview'] }],
  },
  { id: 'settings', label: 'Settings', items: [] },
];

describe('NavComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [NavComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: NotFoundPage }]),
      ],
    }),
  );

  it('renders only provided nonempty groups in a named navigation landmark', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe('Primary navigation');
    expect(root.querySelectorAll('section')).toHaveLength(1);
    expect(root.textContent).toContain('Test design');
    expect(root.textContent).not.toContain('Settings');
    expect(root.querySelectorAll('a')).toHaveLength(1);
  });

  it('marks the active link as the current page and emits link selection', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    const selected = vi.fn();
    fixture.componentInstance.itemSelected.subscribe(selected);
    await fixture.whenStable();
    await TestBed.inject(Router).navigateByUrl('/overview');
    await fixture.whenStable();
    const link = (fixture.nativeElement as HTMLElement).querySelector('a')!;
    expect(link.getAttribute('aria-current')).toBe('page');
    link.click();
    expect(selected).toHaveBeenCalledOnce();
  });

  it('retains accessible names in rail mode and renders no invented items for an empty view', async () => {
    const fixture = TestBed.createComponent(NavComponent);
    fixture.componentRef.setInput('groups', groups);
    fixture.componentRef.setInput('collapsed', true);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.classList.contains('rail')).toBe(true);
    expect(root.querySelector('a')?.getAttribute('aria-label')).toBe('Overview');
    fixture.componentRef.setInput('groups', []);
    await fixture.whenStable();
    expect(root.querySelectorAll('a')).toHaveLength(0);
  });
});
