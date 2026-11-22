import { expect, test } from '@playwright/test';
import { LOCALES } from './support';
import { HEAD_ID, visit } from './settings-policy.support';

// The page body font is not bundled, so a host without it falls back to a narrower sans. The UUID
// field must be sized by its own monospace font, whatever either stack resolves to.
const FALLBACKS = [
  { name: 'narrow sans, wide mono', sans: 'Arial', mono: "'Courier New'" },
  { name: 'narrow sans, default mono', sans: 'Arial', mono: 'monospace' },
] as const;

for (const locale of LOCALES) {
  for (const fallback of FALLBACKS) {
    for (const width of [768, 1024, 1280, 1440]) {
      test(`UUID field does not scroll at ${width}px with ${fallback.name} (${locale})`, async ({
        page,
      }) => {
        await page.setViewportSize({ width, height: 900 });
        await visit(page, locale);
        await page.addStyleTag({
          content: `app-policy-page, app-policy-page * { font-family: ${fallback.sans} !important }
            #policy-revision-id { font-family: ${fallback.mono} !important }`,
        });
        const input = page.locator('#policy-revision-id');
        await expect(input).toHaveValue(HEAD_ID);
        const box = await input.evaluate((el) => ({
          scroll: el.scrollWidth,
          client: el.clientWidth,
          page: document.documentElement.scrollWidth <= innerWidth,
        }));
        expect(box.scroll).toBeLessThanOrEqual(box.client);
        expect(box.page).toBe(true);
        const field = await input.boundingBox();
        const action = await page.locator('[data-policy-open]').boundingBox();
        if (!field || !action) throw new Error('Expected the policy lookup controls');
        expect(field.width).toBeLessThanOrEqual(420);
        expect(field.x + field.width).toBeLessThan(action.x);
        expect(field.y + field.height).toBeCloseTo(action.y + action.height, 0);
      });
    }
  }
}
