export function isTerminalRun(state: string): boolean {
  return state === 'complete' || state === 'incomplete' || state === 'canceled';
}

export function cancelReasonError(reason: string): 'blank' | 'too-long' | null {
  if (reason === '') return null;
  if (reason.trim() === '') return 'blank';
  let length = 0;
  const points = reason[Symbol.iterator]();
  while (!points.next().done) {
    if (++length > 1000) return 'too-long';
  }
  return null;
}
