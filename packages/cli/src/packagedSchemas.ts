// The schema registry a command validates against: the texts compiled into the package by
// the build (`scripts/embed-contracts.mjs`), not a directory found on disk. A package
// install has no repository above it, so the only supported way to reach the contract
// schemas is this module — never a path search.
import { contractSchemaTexts } from './contracts.generated.js';
import { compileSchemaDocuments, type ContractSchemaSet, type SchemaDocument } from './validate.js';

/**
 * Every contract schema, compiled from the embedded texts. There is no file-system access
 * here at all; the parity spec pins the texts to the repository `contracts/` tree.
 */
export function loadPackagedContractSchemas(): ContractSchemaSet {
  const documents = new Map<string, SchemaDocument>();
  for (const [key, text] of Object.entries(contractSchemaTexts)) {
    documents.set(key, { file: `embedded:${key}`, text });
  }
  return compileSchemaDocuments(documents);
}
