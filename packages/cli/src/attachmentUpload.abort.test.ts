// The drain signal at its first checkpoint: an already-aborted call opens nothing and requests
// nothing, so an abandoned attachment costs no descriptor and no token exchange.
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import {
  harness,
  input,
  json,
  rejection,
  useAttachmentUploadSpec,
} from './attachmentUpload.testing.js';
import { CliError } from './errors.js';

describe('createAttachmentUploader', () => {
  const { tempRoot, uploader } = useAttachmentUploadSpec();

  // The drain signal is read BEFORE the file is opened: the path is outside the allowed roots
  // and does not exist, so `upload_aborted` (not `upload_path_refused`/`upload_unreadable`)
  // proves nothing was opened and no request was made.
  it('opens_nothing_and_requests_nothing_when_the_signal_is_already_aborted', async () => {
    const outside = await tempRoot('evidgate-attach-aborted-');
    const path = join(outside, 'never-opened.zip');
    const controller = new AbortController();
    controller.abort();
    const store = harness(() => json(200, {}));

    const error = await rejection(
      uploader(store).upload(input(path), { signal: controller.signal }),
    );

    expect(error).toBeInstanceOf(CliError);
    expect(error.code).toBe('upload_aborted');
    expect(store.calls).toHaveLength(0);
    // The refusal carries the code and a fixed message: no path, no token.
    expect(error.message).not.toContain(outside);
  });
});
