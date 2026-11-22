import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
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
}

describe('Shell account menu', () => {
  it('delegates an explicit sign-out to the existing session boundary', async () => {
    const logout = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      imports: [ShellAccountComponent],
      providers: [{ provide: SessionStore, useValue: { logout } }],
    });
    const fixture = TestBed.createComponent(ShellAccountComponent);
    const account = await TestbedHarnessEnvironment.harnessForFixture(fixture, Account);
    await (await account.trigger()).click();
    const menu =
      await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(AccountMenu);
    expect(logout).not.toHaveBeenCalled();
    const item = await menu.signOut();
    expect(await item.text()).toBe('Sign out of this tab');
    await item.click();
    expect(logout).toHaveBeenCalledExactlyOnceWith();
  });
});
