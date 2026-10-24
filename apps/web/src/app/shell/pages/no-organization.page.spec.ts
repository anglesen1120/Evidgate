import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NoOrganizationPage } from './no-organization.page';

describe('NoOrganizationPage', () => {
  it('has one heading and one home action, without inventing onboarding functionality', async () => {
    await TestBed.configureTestingModule({
      imports: [NoOrganizationPage],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(NoOrganizationPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('h1')?.textContent).toBe('No organization yet');
    expect(root.querySelectorAll('a')).toHaveLength(1);
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/');
    expect(root.querySelector('form')).toBeNull();
  });
});
