// 01.3.2-T1.2: the public surface of `@evidgate/sdk` (00 §5.6 W5).
//
// `export *` re-exports every generated operation (`listRuns`, `createRun`, …), the
// per-call `Options` type and every schema type — including `ErrorEnvelope` and
// `CursorList` (00 §9.2/§9.1). The generated tree under `../gen/` is the source of truth
// (AC1: hand-edits fail `scripts/check-codegen-drift.sh`); `src/` only adds the
// handwritten hook/envelope surface below.
export * from '../gen/index.js';

// Generated client machinery — not re-exported by `gen/index.js`, so it is surfaced
// explicitly here. `Config`/`CreateClientConfig` build client configurations
// (`createConfig`), `client` is the default client the operations fall back to, and the
// `Client`/options types type the `client.interceptors` request path the header hooks
// plug into (`./headers.js`).
export { client } from '../gen/client.gen.js';
export type { CreateClientConfig } from '../gen/client.gen.js';
export {
  createClient,
  createConfig,
  mergeHeaders,
  type Client,
  type ClientMeta,
  type Config,
  type RequestResult,
  type ResolvedRequestOptions,
} from '../gen/client/index.js';

// Handwritten surface (01.3.2-T1.2/T1.4): §9.2 envelope helpers and the W5 header hooks.
export type { SdkHeaderHook, SdkHeaderHookContext, SdkHeaderHooks } from './headers.js';
export { createHeaderHookInterceptor } from './headers.js';
export { isErrorEnvelope, type ErrorEnvelopeBody, type ErrorEnvelopeDetail } from './errors.js';
