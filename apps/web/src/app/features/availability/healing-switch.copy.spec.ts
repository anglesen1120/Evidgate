import { expect } from 'vitest';

import { HEALING_SWITCH_COPY, modeLabel } from './healing-switch.copy';

describe('healing switch copy', () => {
  it('labels every mode a row or a resolution can name', () => {
    expect(modeLabel('enabled')).toBe(HEALING_SWITCH_COPY.modeEnabled);
    expect(modeLabel('shadow')).toBe(HEALING_SWITCH_COPY.modeShadow);
    expect(modeLabel('disabled')).toBe(HEALING_SWITCH_COPY.modeDisabled);
    expect(modeLabel('unavailable')).toBe(HEALING_SWITCH_COPY.modeUnavailable);
  });
});
