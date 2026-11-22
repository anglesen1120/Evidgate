import type { StatusTone } from '../../../shared/ui/chips/status-pill.component';
import type { GeneratedScriptsState } from './generated-scripts.states';
import type { ReviewMetricViewModel } from './scripts-review-state';
import { scriptCopy as c } from './generated-scripts.copy';

interface ScriptStepViewModel {
  readonly number: string;
  readonly action: string;
  readonly binding: string;
  readonly reason: string | null;
  readonly rule: string | null;
  readonly lint: string;
}
interface StabilityAttemptViewModel {
  readonly number: string;
  readonly build: string | null;
  readonly result: string;
  readonly detail: string;
}
export interface ScriptRowViewModel {
  readonly id: string;
  readonly manualCase: string;
  readonly revision: string;
  readonly title: string;
  readonly automated: string | null;
  readonly status: string;
  readonly tone: StatusTone;
  readonly stability: string;
  readonly pr: string;
  readonly prNumber: string | null;
  readonly steps: readonly ScriptStepViewModel[];
  readonly attempts: readonly StabilityAttemptViewModel[];
  readonly source: string;
}
export interface GeneratedScriptsViewModel {
  readonly status: GeneratedScriptsState;
  readonly project: string;
  readonly asOf: string;
  readonly runContext: string;
  readonly ci: string;
  readonly generationCommand: string;
  readonly annotationKey: string;
  readonly earlierBuild: string;
  readonly metrics: readonly ReviewMetricViewModel[];
  readonly scripts: readonly ScriptRowViewModel[];
}

const bound = (number: string, action: string, rule: string): ScriptStepViewModel => ({
  number,
  action,
  binding: c.bound,
  reason: null,
  rule,
  lint: c.green,
});
const unbound = (number: string, action: string, reason: string): ScriptStepViewModel => ({
  number,
  action,
  binding: c.notBound,
  reason,
  rule: null,
  lint: c.unavailable,
});
export const generatedScriptsFixture: GeneratedScriptsViewModel = {
  status: 'ready',
  project: 'storefront-web · SHOP · p-storefront',
  asOf: '2026-10-23T10:00:00Z',
  runContext: 'run-1842 · ev-3',
  ci: 'run-1842 · GitHub Actions',
  generationCommand: 'evidgate generate',
  annotationKey: 'evidgate.id',
  earlierBuild: 'b-317',
  metrics: [
    { title: c.testability, value: '72 / 100', detail: c.measured, icon: 'lucideActivity' },
    { title: c.active, value: '1', detail: c.sameBuild, icon: 'lucideCircleCheck' },
    { title: c.unbound, value: '2', detail: c.unboundDetail, icon: 'lucideInfo' },
    { title: c.flaky, value: '1', detail: c.retained, icon: 'lucideClock' },
  ],
  scripts: [
    {
      id: 'gs-0042',
      manualCase: 'SHOP-TC-0042',
      revision: 'v4',
      title: c.checkout,
      automated: 'case-checkout-17',
      status: c.active,
      tone: 'success',
      stability: c.stable,
      pr: c.merged,
      prNumber: '#214',
      steps: [
        bound('01', c.openCheckout, 'role=button[name=Place order]'),
        bound('02', c.place, 'label=Email'),
        bound('03', c.confirm, 'testid=confirmation-heading'),
      ],
      attempts: ['1', '2', '3'].map((number) => ({
        number,
        build: 'b-318',
        result: 'success → passed',
        detail: c.ok,
      })),
      source: `await page.goto('/checkout');
await page.getByRole('button', { name: 'Place order' }).click();
await expect(page.getByTestId('confirmation-heading')).toBeVisible();`,
    },
    {
      id: 'gs-0043',
      manualCase: 'SHOP-TC-0043',
      revision: 'v2',
      title: c.address,
      automated: null,
      status: c.draft,
      tone: 'neutral',
      stability: c.unboundCount,
      pr: c.notCreated,
      prNumber: null,
      steps: [
        bound('01', c.openCheckout, 'role=button[name=Continue]'),
        unbound('02', c.fill, c.noLocator),
        unbound('03', c.confirm, c.noBinding),
      ],
      attempts: [],
      source: `await page.goto('/checkout');`,
    },
    {
      id: 'gs-0051',
      manualCase: 'SHOP-TC-0051',
      revision: 'v2',
      title: c.discount,
      automated: 'case-total-2',
      status: c.flaky,
      tone: 'info',
      stability: c.retryBuild,
      pr: c.open,
      prNumber: '#219',
      steps: [
        bound('01', c.openCheckout, 'role=button[name=Apply]'),
        bound('02', c.apply, 'text="Apply discount"'),
        bound('03', c.total, 'testid=order-total'),
      ],
      attempts: [
        { number: '1', build: 'b-318', result: 'failure → failed', detail: c.staleSubtotal },
        { number: '1 ↻', build: 'b-318', result: 'success → passed', detail: c.retryOk },
        { number: '2', build: 'b-318', result: 'failure → failed', detail: c.staleSubtotal },
        { number: '3', build: null, result: c.unavailable, detail: c.missingAttempt },
      ],
      source: `await page.goto('/checkout');
await page.getByRole('button', { name: 'Apply' }).click();
await expect(page.getByTestId('order-total')).toHaveText(expectedTotal);`,
    },
  ],
};
