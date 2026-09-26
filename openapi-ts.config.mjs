// 01.3.2-T1.1: the single generator invocation for `packages/sdk`.
//
// The spec (`contracts/openapi.yaml`, 01.3.1) is the source of truth; `packages/sdk/gen`
// is a committed build product, never hand-edited (AC1 pins that with
// `scripts/check-codegen-drift.sh`). Everything that could make two runs differ is pinned
// here, so `npm run codegen` on a clean tree is byte-for-byte reproducible:
//
// - `output.clean` keeps stale files out of the tree (deleting a schema must delete its
//   generated file, otherwise the drift gate could never see it);
// - `output.postProcess` is left empty — no prettier/eslint pass runs over the output, so
//   the tree does not depend on a formatter version or on the repo's own prettier config;
//   `gen/` is prettier-ignored instead (see .prettierignore) for the same reason
//   `fixtures/rfc8785/{input,output}` are;
// - `output.module.extension` is pinned to `.js` so the emitted ESM import specifiers match
//   the workspace's `module`/`moduleResolution: NodeNext` (tsconfig.base.json) regardless of
//   which tsconfig the generator would have discovered;
// - `logs.file: false` stops the generator from writing a timestamped log file into the
//   working tree.
//
// The HTTP client is Hey API's `@hey-api/client-fetch`, bundled into the output
// (`bundle: true` is its default) — `packages/sdk` must not depend on a separate client
// runtime package, because the SDK is consumed by both `packages/cli` (Node) and
// `apps/web` (browser).
import { defineConfig } from '@hey-api/openapi-ts';

export default defineConfig({
  // `./`-prefixed so the input is read as a local file — a bare `org/file` shape is
  // parsed as a Hey API registry shorthand by the generator.
  input: './contracts/openapi.yaml',
  output: {
    path: 'packages/sdk/gen',
    clean: true,
    module: { extension: '.js' },
  },
  plugins: ['@hey-api/client-fetch', '@hey-api/typescript', '@hey-api/sdk'],
  logs: { file: false },
});
