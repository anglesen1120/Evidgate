// The stored browser state of the seeded fixture tenant. It is keyed on the shard so shards that
// share one machine never read a file another shard is still writing.
const shard = process.env['EVIDGATE_E2E_CORE_SHARD'];

export const AUTH_STATE = `test-results/core-auth-state${shard === undefined || shard === '' ? '' : `-${shard}`}.json`;
