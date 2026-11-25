import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { IdentityDataService } from '../../core/data/identity-data.service';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { SessionStore } from '../../core/session/session.store';
import { ShellAccountComponent } from './shell-account.component';

provideLayoutTestStubs();

class Account extends ComponentHarness {
  static hostSelector = 'app-shell-account';
  readonly trigger = this.locatorFor('[aria-haspopup="menu"]');
}
class AccountMenu extends ComponentHarness {
  static hostSelector = '[role="menu"]';
  readonly signOut = this.locatorFor('[role="menuitem"]');
  readonly themeOptions = this.locatorForAll('[role="menuitemradio"]');
  readonly name = this.locatorForOptional('bdi');
}

describe('Shell account menu', () => {
  const profile = vi.fn();
  const logout = vi.fn();

  beforeEach(() => {
    profile.mockReset().mockResolvedValue({ display_name: 'Linh Nguyễn' });
    logout.mockReset().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      imports: [ShellAccountComponent],
      providers: [
        { provide: SessionStore, useValue: { logout } },
        { provide: IdentityDataService, useValue: { getMyProfile: profile } },
      ],
    });
  });

  async function openMenu() {
    const fixture = TestBed.createComponent(ShellAccountComponent);
    const account = await TestbedHarnessEnvironment.harnessForFixture(fixture, Account);
    expect(profile).not.toHaveBeenCalled();
    await (await account.trigger()).click();
    return TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(AccountMenu);
  }

  it('displays the returned name inside the menu and offers three theme choices', async () => {
    const menu = await openMenu();
    expect(await (await menu.name())?.text()).toBe('Linh Nguyễn');
    const options = await menu.themeOptions();
    expect(await Promise.all(options.map((option) => option.text()))).toEqual([
      'Light',
      'Dark',
      'System',
    ]);
    await options[1]!.click();
    expect(await options[1]!.getAttribute('aria-checked')).toBe('true');
    await options[2]!.click();
    expect(await options[2]!.getAttribute('aria-checked')).toBe('true');
    expect(await options[1]!.getAttribute('aria-checked')).toBe('false');
  });

  it('keeps preferences and sign-out available without guessing a failed profile', async () => {
    profile.mockRejectedValue(new Error('unavailable'));
    const menu = await openMenu();
    expect(await menu.name()).toBeNull();
    expect(await menu.themeOptions()).toHaveLength(3);
    expect(await (await menu.signOut()).text()).toBe('Sign out of this tab');
  });

  it('delegates an explicit sign-out to the existing session boundary', async () => {
    const menu = await openMenu();
    expect(logout).not.toHaveBeenCalled();
    const item = await menu.signOut();
    expect(await item.text()).toBe('Sign out of this tab');
    await item.click();
    expect(logout).toHaveBeenCalledExactlyOnceWith();
  });
});
