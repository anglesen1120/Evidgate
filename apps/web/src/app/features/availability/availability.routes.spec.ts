import { HealingSwitchPage } from './healing-switch.page';
import { routes } from './availability.routes';

describe('availability route table', () => {
  it('mounts the healing switch page on the empty path', () => {
    expect(routes).toHaveLength(1);
    expect(routes[0]).toEqual({ path: '', pathMatch: 'full', component: HealingSwitchPage });
  });
});
