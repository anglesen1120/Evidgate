import { boundaryDelayMs } from './health-boundary';

const NOW = 1_767_225_600_000;
const TIMER_CEILING = 2 ** 31 - 1;

describe('boundaryDelayMs', () => {
  it('waits the whole distance to a boundary ahead of now', () => {
    expect(boundaryDelayMs(NOW + 5, NOW)).toBe(5);
  });

  it('asks for an immediate re-arm when the boundary is now', () => {
    expect(boundaryDelayMs(NOW, NOW)).toBe(0);
  });

  it('keeps a negative delay when the boundary is already behind now', () => {
    expect(boundaryDelayMs(NOW - 5, NOW)).toBe(-5);
  });

  it.each([TIMER_CEILING - 1, TIMER_CEILING])(
    'keeps a delay of %i ms, which is inside the timer ceiling',
    (delay) => {
      expect(boundaryDelayMs(NOW + delay, NOW)).toBe(delay);
    },
  );

  it('clamps one millisecond past the ceiling, so the wait happens in capped steps', () => {
    expect(boundaryDelayMs(NOW + TIMER_CEILING + 1, NOW)).toBe(TIMER_CEILING);
  });

  // `arm()` never arms with `Infinity`: it returns before scheduling when no boundary is left. The
  // clamp is asserted anyway, because it is what keeps any future caller from arming a null timer.
  it('clamps an unbounded distance to the ceiling', () => {
    expect(boundaryDelayMs(Infinity, NOW)).toBe(TIMER_CEILING);
  });

  // `nextBoundary` only returns a parsed timestamp or `Infinity`, so `arm()` cannot reach `NaN`.
  // The arithmetic is kept as it was: a `NaN` boundary stays a `NaN` delay rather than becoming
  // an immediate or a capped wait that hides a broken timestamp.
  it('propagates NaN rather than turning a broken boundary into a wait', () => {
    expect(boundaryDelayMs(NaN, NOW)).toBeNaN();
    expect(boundaryDelayMs(NOW, NaN)).toBeNaN();
  });
});
