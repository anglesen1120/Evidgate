import { expect } from 'vitest';

import { HEALING_SWITCH_COPY, healthLabel, modeLabel } from './healing-switch.copy';

describe('healing switch copy', () => {
  it('labels every mode a row or a resolution can name', () => {
    expect(modeLabel('enabled')).toBe(HEALING_SWITCH_COPY.modeEnabled);
    expect(modeLabel('shadow')).toBe(HEALING_SWITCH_COPY.modeShadow);
    expect(modeLabel('disabled')).toBe(HEALING_SWITCH_COPY.modeDisabled);
    expect(modeLabel('unavailable')).toBe(HEALING_SWITCH_COPY.modeUnavailable);
  });

  it('labels the known dependency observations and leaves unknown values unlabelled', () => {
    expect(healthLabel('ok')).toBe(HEALING_SWITCH_COPY.healthHealthy);
    expect(healthLabel('healthy')).toBe(HEALING_SWITCH_COPY.healthHealthy);
    expect(healthLabel('degraded')).toBe(HEALING_SWITCH_COPY.healthDegraded);
    expect(healthLabel('unavailable')).toBe(HEALING_SWITCH_COPY.healthUnavailable);
    expect(healthLabel(null)).toBeNull();
    expect(healthLabel('warming-up')).toBeNull();
  });
});
