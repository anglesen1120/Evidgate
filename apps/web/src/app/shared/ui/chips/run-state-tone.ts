import type { StatusTone } from './status-pill.component';

/** Canonical lifecycle values from plans/00-overview.md §8.1, not release decisions. */
export type RunState =
  'created' | 'queued' | 'running' | 'collecting' | 'complete' | 'incomplete' | 'canceled';

/**
 * O1: complete means collection complete, never a passing release verdict.
 * The run-state pill always includes its Run status dimension and uses recipe B,
 * not the bordered release-decision chip. No lifecycle value maps to danger.
 */
export const RUN_STATE_TONES = {
  created: 'neutral',
  queued: 'neutral',
  running: 'info',
  collecting: 'info',
  complete: 'success',
  incomplete: 'warning',
  canceled: 'neutral',
} as const satisfies Readonly<Record<RunState, StatusTone>>;

export function isRunState(value: string): value is RunState {
  return Object.hasOwn(RUN_STATE_TONES, value);
}
