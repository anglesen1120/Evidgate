export function securityEventRange(fromValue: string, toValue: string) {
  const from = fromValue ? new Date(fromValue) : null;
  const to = toValue ? new Date(toValue) : null;
  const invalidFrom = from !== null && Number.isNaN(from.getTime());
  const invalidTo = to !== null && Number.isNaN(to.getTime());
  const unordered = from !== null && to !== null && !invalidFrom && !invalidTo && from >= to;
  return { from, to, invalidFrom: invalidFrom || unordered, invalidTo: invalidTo || unordered };
}
