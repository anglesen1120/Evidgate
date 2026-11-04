// Creation-time resolution: a surface that cannot resolve is the CLI's own `missing_config`,
// and a malformed upload deadline is `invalid_config` — both raised at creation, so a run fails
// before its first test rather than after it, and the deadline resolves once, not per
// attachment.
import { describe, expect, it } from 'vitest';

import { createAttachmentUploader } from './attachmentUpload.js';
import { harness, json, useAttachmentUploadSpec } from './attachmentUpload.testing.js';
import { CliError } from './errors.js';

describe('createAttachmentUploader', () => {
  let root = '';
  const { env } = useAttachmentUploadSpec((directory) => {
    root = directory;
  });

  // AC: a surface that cannot resolve is the CLI's own `missing_config`, raised at creation
  // so a run fails before its first test rather than after it.
  it('refuses_to_be_created_without_an_api_url_or_a_token_source', () => {
    const store = harness(() => json(200, {}));
    for (const unset of [
      env({ EVIDGATE_API_URL: undefined }),
      env({ EVIDGATE_DEV_TOKEN: undefined }),
    ]) {
      let thrown: unknown;
      try {
        createAttachmentUploader({ env: unset, dirs: [root], cwd: root, fetch: store.fetch });
      } catch (error) {
        thrown = error;
      }
      expect(thrown).toBeInstanceOf(CliError);
      expect((thrown as CliError).code).toBe('missing_config');
    }
    expect(store.calls).toHaveLength(0);
  });

  // Boundary of the same resolution: a malformed deadline is `invalid_config` at creation,
  // resolved once instead of per attachment.
  it('refuses_a_malformed_upload_deadline_at_creation', () => {
    const store = harness(() => json(200, {}));
    let thrown: unknown;
    try {
      createAttachmentUploader({
        env: env({ EVIDGATE_UPLOAD_DEADLINE: 'soon' }),
        dirs: [root],
        cwd: root,
        fetch: store.fetch,
      });
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(CliError);
    expect((thrown as CliError).code).toBe('invalid_config');
    // An explicit deadline is a caller decision: the malformed env value is not consulted.
    expect(() =>
      createAttachmentUploader({
        env: env({ EVIDGATE_UPLOAD_DEADLINE: 'soon' }),
        dirs: [root],
        cwd: root,
        fetch: store.fetch,
        deadlineMs: 1_000,
      }),
    ).not.toThrow();
  });
});
