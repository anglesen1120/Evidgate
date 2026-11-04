// The package graph: production sources of `@evidgate/cli` never import the Playwright
// adapter. Comments may name it; an import specifier may not, or the dependency arrow would
// point both ways.
import { readFile } from 'node:fs/promises';
import { relative } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { productionSources } from './attachmentUpload.testing.js';

/** The forbidden dependency edge: the CLI must never import the reporter's package. */
const forbiddenAdapterImport =
  /(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*)['"][^'"]*(?:adapter-playwright|playwright-reporter)[^'"]*['"]/u;

describe('createAttachmentUploader', () => {
  // Boundary of the package graph (the reporter consumes the CLI): production sources of
  // `@evidgate/cli` never import the Playwright adapter. Comments may name it; an import
  // specifier may not, or the dependency arrow would point both ways.
  it('keeps_the_production_sources_free_of_adapter_imports', async () => {
    const sourceRoot = fileURLToPath(new URL('.', import.meta.url));
    const sources = await productionSources(sourceRoot);
    expect(sources.length).toBeGreaterThan(10);

    const offenders: string[] = [];
    for (const file of sources) {
      const text = await readFile(file, 'utf8');
      if (forbiddenAdapterImport.test(text)) {
        offenders.push(relative(sourceRoot, file));
      }
    }

    expect(offenders).toEqual([]);
  });
});
