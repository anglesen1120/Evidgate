import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import type { OutputEmitterRef, Type } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import { DashboardWidgetComponent } from '../../../shared/ui/dashboard/dashboard-widget.component';
import { DashboardMetricComponent } from './dashboard-metric.component';

class DashboardRetryHarness extends ComponentHarness {
  static hostSelector = 'app-dashboard-widget, app-dashboard-metric';
  readonly retry = this.locatorFor('button');
  readonly retryOrNull = this.locatorForOptional('button');
  readonly heading = this.locatorFor('h2[tabindex="-1"], section[tabindex="-1"]');
  readonly loading = this.locatorForOptional('app-loading-pattern');
}

interface RetrySource {
  readonly retry: OutputEmitterRef<void>;
}

const consumers: readonly {
  component: Type<RetrySource>;
  inputName: string;
  title: string;
}[] = [
  { component: DashboardWidgetComponent, inputName: 'title', title: 'Recent runs' },
  { component: DashboardMetricComponent, inputName: 'label', title: 'Active cases' },
];

for (const consumer of consumers) {
  describe(`${consumer.component.name} retry focus`, () => {
    let fixture: ComponentFixture<RetrySource>;
    let root: HTMLElement;
    let harness: DashboardRetryHarness;

    beforeEach(async () => {
      fixture = TestBed.createComponent(consumer.component);
      root = fixture.nativeElement as HTMLElement;
      document.body.append(root);
      fixture.componentRef.setInput(consumer.inputName, consumer.title);
      fixture.componentRef.setInput('error', {
        kind: 'unavailable',
        status: 503,
        code: 'busy',
        message: 'Try later',
        requestId: null,
        retryable: true,
        retryAfterSeconds: null,
        details: [],
      });
      harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, DashboardRetryHarness);
    });

    afterEach(() => {
      fixture.destroy();
      root.remove();
    });

    it('focuses its persistent named target before each local retry starts loading', async () => {
      let retries = 0;
      let focusedAtRetry: Element | null = null;
      fixture.componentInstance.retry.subscribe(() => {
        retries++;
        focusedAtRetry = document.activeElement;
        fixture.componentRef.setInput('loading', true);
        fixture.detectChanges();
      });
      const heading = await harness.heading();
      expect((await heading.getAttribute('aria-label')) ?? (await heading.text())).toBe(
        consumer.title,
      );
      expect(await heading.getAttribute('tabindex')).toBe('-1');

      for (const expectedRetries of [1, 2]) {
        fixture.componentRef.setInput('loading', false);
        const retry = await harness.retry();
        await retry.focus();
        expect(await retry.isFocused()).toBe(true);
        await retry.click();

        expect(retries).toBe(expectedRetries);
        expect(await harness.loading()).not.toBeNull();
        expect(await harness.retryOrNull()).toBeNull();
        expect(await heading.isFocused()).toBe(true);
        expect(document.activeElement).toBe(focusedAtRetry);
        expect(document.activeElement).not.toBe(document.body);
        expect(document.activeElement?.isConnected).toBe(true);
        expect(
          document.activeElement?.getAttribute('aria-label') ??
            document.activeElement?.textContent?.trim(),
        ).toBe(consumer.title);
      }
    });

    it('does not move focus or emit retry for an external loading update', async () => {
      let retries = 0;
      fixture.componentInstance.retry.subscribe(() => {
        retries++;
      });
      const outside = document.createElement('button');
      outside.textContent = 'Another action';
      document.body.append(outside);
      try {
        outside.focus();
        fixture.componentRef.setInput('loading', true);
        expect(await harness.loading()).not.toBeNull();
        expect(document.activeElement).toBe(outside);
        expect(retries).toBe(0);
      } finally {
        outside.remove();
      }
    });
  });
}
