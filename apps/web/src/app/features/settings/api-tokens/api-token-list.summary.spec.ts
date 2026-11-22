import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { ApiTokenView } from '@evidgate/sdk';
import { ApiTokenListComponent } from './api-token-list.component';
import { ROW, TOKEN_DESKTOP_PROVIDER } from './api-tokens.test-support';

class TokenSummaryHarness extends ComponentHarness {
  static hostSelector = 'app-api-token-list';
  readonly values = this.locatorForAll('app-settings-page-counts app-number-value');
  readonly search = this.locatorFor('#api-token-search');
  readonly inspect = this.locatorForAll('[data-api-inspect]');
  readonly detail = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');

  async counts(): Promise<string[]> {
    return Promise.all((await this.values()).map((value) => value.text()));
  }

  async filter(value: string): Promise<void> {
    const search = await this.search();
    await search.setInputValue(value);
    await search.dispatchEvent('input');
  }
}

describe('API token loaded-page summary boundaries', () => {
  afterEach(() => TestBed.resetTestingModule());

  async function render(rows: readonly ApiTokenView[]) {
    TestBed.configureTestingModule({
      imports: [ApiTokenListComponent],
      providers: [TOKEN_DESKTOP_PROVIDER],
    });
    const fixture = TestBed.createComponent(ApiTokenListComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.detectChanges();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, TokenSummaryHarness);
    return { fixture, harness };
  }

  it('keeps all four counts at zero for an empty loaded page', async () => {
    const { harness } = await render([]);
    expect(await harness.counts()).toEqual(['0', '0', '0', '0']);
    expect(await harness.inspect()).toHaveLength(0);
    expect(await harness.detail()).toBeNull();
  });

  it('counts served states without inferring expiry from past dates', async () => {
    const { harness } = await render([
      { ...ROW, id: 'active-one', expires_at: '2000-01-01T00:00:00Z' },
      { ...ROW, id: 'active-two', expires_at: '2000-01-01T00:00:00Z' },
      { ...ROW, id: 'expired', status: 'expired', expires_at: '2000-01-01T00:00:00Z' },
      { ...ROW, id: 'revoked', status: 'revoked', expires_at: '2000-01-01T00:00:00Z' },
    ]);
    expect(await harness.counts()).toEqual(['4', '2', '1', '1']);
  });

  it('retains loaded-page totals when a local search has no matching metadata', async () => {
    const { harness } = await render([
      ROW,
      { ...ROW, id: 'revoked', name: 'Revoked reader', status: 'revoked' },
    ]);
    expect(await harness.counts()).toEqual(['2', '1', '0', '1']);
    expect(await harness.inspect()).toHaveLength(2);
    await harness.filter('not-on-this-page');
    expect(await harness.counts()).toEqual(['2', '1', '0', '1']);
    expect(await harness.inspect()).toHaveLength(0);
    expect(await harness.detail()).toBeNull();
  });

  it('replaces previous-page counts and selected metadata when the loaded page changes', async () => {
    const { fixture, harness } = await render([
      ROW,
      { ...ROW, id: 'old-expired', status: 'expired' },
    ]);
    expect(await harness.counts()).toEqual(['2', '1', '1', '0']);
    await (await harness.inspect())[0]?.click();
    expect(await (await harness.detail())?.text()).toContain('nightly-reader');
    fixture.componentRef.setInput('rows', [
      { ...ROW, id: 'new-revoked', name: 'New page reader', status: 'revoked' },
    ]);
    expect(await harness.counts()).toEqual(['1', '0', '0', '1']);
    expect(await harness.detail()).toBeNull();
    await (await harness.inspect())[0]?.click();
    const detail = await (await harness.detail())?.text();
    expect(detail).toContain('New page reader');
    expect(detail).not.toContain('nightly-reader');
  });
});
