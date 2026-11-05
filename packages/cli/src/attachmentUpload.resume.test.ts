// A restarted shard: with a spool the journal is opened per `(run, producer)`, so the second
// call resumes the artifact rows of the first instead of minting a new one — it answers from
// the terminal record with no request at all.
import { describe, expect, it } from 'vitest';

import {
  acceptingStore,
  artifactId,
  digestOfFile,
  harness,
  input,
  useAttachmentUploadSpec,
} from './attachmentUpload.testing.js';

describe('createAttachmentUploader', () => {
  const { attachment, env, tempRoot, uploader } = useAttachmentUploadSpec();

  it('resumes_a_journaled_upload_without_a_second_artifact_row', async () => {
    const { path, content } = await attachment('trace.zip');
    const spoolDir = await tempRoot('evidgate-attach-spool-');
    const spoolEnv = env({ EVIDGATE_SPOOL_DIR: spoolDir });

    const first = acceptingStore(await digestOfFile(path));
    const uploaded = await uploader(first, { env: spoolEnv }).upload(input(path));
    expect(uploaded).toEqual({ artifactId, bytes: content.byteLength });
    expect(first.calls.map((call) => call.method)).toEqual(['POST', 'POST', 'PUT', 'POST']);

    const restarted = harness(() => {
      throw new Error('a resumed upload must not make a request');
    });
    const resumed = await uploader(restarted, { env: spoolEnv }).upload(input(path));
    expect(resumed).toEqual({ artifactId, bytes: content.byteLength });
    expect(restarted.calls).toHaveLength(0);
  });
});
