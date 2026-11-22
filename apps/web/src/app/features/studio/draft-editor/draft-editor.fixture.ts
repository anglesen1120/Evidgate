import { editorCopy as c } from './editor-copy';

const scenes = [
  'default',
  'loading',
  'empty',
  'error',
  '401',
  '403',
  '404',
  '412',
  '429',
  'dirty',
  'busy',
  'saved',
  'invalid',
  'readonly',
  'published',
  'merged',
  'running',
  'failed',
  'passed',
  'aiempty',
  'aiout',
] as const;
export type EditorScene = (typeof scenes)[number];
export function editorScene(value: string | null): EditorScene {
  if (value === 'saving') return 'busy';
  return scenes.find((scene) => scene === value) ?? 'default';
}

export type DefinitionField = 'intent' | 'pre' | 'action' | 'locator' | 'assertion' | 'cleanup';
export interface StudioDraftEditorViewModel {
  readonly project: string;
  readonly stableCase: string;
  readonly author: string;
  readonly base: string;
  readonly revision: string;
  readonly hash: string;
  readonly reviewed: {
    readonly revision: string;
    readonly hash: string;
    readonly assertion: string;
  };
  readonly fields: readonly {
    readonly id: DefinitionField;
    readonly label: string;
    readonly value: string;
  }[];
  readonly variants: readonly string[];
  readonly lint: readonly { readonly label: string; readonly sample: string }[];
  readonly ai: { readonly id: string; readonly sample: string };
  readonly sandbox: {
    readonly execution: string;
    readonly artifact: string;
    readonly script: string;
  };
  readonly revisions: readonly {
    readonly id: string;
    readonly version: string;
    readonly status: 'approved' | 'awaiting' | 'drafting';
  }[];
  readonly discountSteps: readonly {
    readonly number: number;
    readonly action?: string;
    readonly expected?: string;
  }[];
}

export const draftEditorFixture: StudioDraftEditorViewModel = {
  project: 'SHOP · p-storefront',
  stableCase: 'case-checkout-17',
  author: 'Linh Nguyễn',
  base: 'cr-9 / 9af317c',
  revision: 'dr-4',
  hash: 'ah-92fd…6241',
  reviewed: { revision: 'dr-5', hash: 'ah-1f9b…473a', assertion: c.updatedAssertion },
  fields: [
    { id: 'intent', label: c.intent, value: c.intentValue },
    { id: 'pre', label: c.pre, value: c.preValue },
    { id: 'action', label: c.action, value: c.actionValue },
    { id: 'locator', label: c.locator, value: "getByTestId('place-order')" },
    { id: 'assertion', label: c.assertion, value: c.assertionValue },
    { id: 'cleanup', label: c.cleanup, value: c.cleanupValue },
  ],
  variants: ['chromium / vi', 'firefox / en'],
  lint: [
    { label: c.forbiddenChange, sample: 'force: true' },
    { label: c.weaken, sample: 'toBeVisible() → toBeTruthy()' },
    { label: c.skip, sample: 'test.skip · test.fixme' },
  ],
  ai: { id: 'ai-21', sample: "getByRole('button', {name: 'Place order'})" },
  sandbox: {
    execution: 'dry-114 / run-sandbox-88',
    artifact: 'ev-s1',
    script: [
      "test('checkout receipt shows the total', async ({ page }) => {",
      "  await page.goto('/checkout');",
      "  await page.getByTestId('place-order').click();",
      "  const receipt = page.getByTestId('order-receipt');",
      '  await expect(receipt).toBeVisible();',
      '  await expect(receipt).toContainText(orderTotal);',
      '  await expect(receipt).toContainText(orderId);',
      '});',
    ].join('\n'),
  },
  revisions: [
    { id: 'SHOP-TC-0051', version: 'v2', status: 'approved' },
    { id: 'SHOP-TC-0051', version: 'v3', status: 'awaiting' },
    { id: 'SHOP-TC-0042', version: 'v4', status: 'approved' },
    { id: 'SHOP-TC-0042', version: 'v5', status: 'drafting' },
  ],
  discountSteps: [
    { number: 1 },
    { number: 2 },
    { number: 3 },
    { number: 4 },
    { number: 5 },
    { number: 6 },
    { number: 7, action: c.step7, expected: c.stepResult },
  ],
};
