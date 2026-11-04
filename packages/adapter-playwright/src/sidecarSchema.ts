// Embedded copy of contracts/catalog/sidecar.v1.json: the reporter ships standalone, so the
// repository's contracts/ directory is not present at runtime. sidecar.test.ts asserts this
// literal deep-equals the contract file, so the two cannot drift silently.
export const SIDECAR_SCHEMA_V1: Record<string, unknown> = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  $id: 'sidecar.v1.json',
  title: 'Case sidecar manifest v1',
  description:
    "The evidgate.cases.yaml file that sits next to the test files it describes (03.1.1, TL T55): it maps each test file of its own directory to the durable evidgate.id of the case it holds. The YAML is converted to JSON and validated against this schema; semantic checks (the file exists in the tree, no duplicate file entry, no second source) are the reader's. The file is parsed as plain data: anchors, tags and merge keys are refused before this point.",
  type: 'object',
  additionalProperties: false,
  required: ['version', 'cases'],
  properties: {
    version: { const: 1 },
    cases: {
      type: 'array',
      maxItems: 5000,
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['file', 'evidgate.id'],
        properties: {
          file: {
            type: 'string',
            minLength: 1,
            maxLength: 1024,
            description:
              "Path relative to the manifest's own directory, forward slashes, no leading slash, no '..' segment.",
          },
          'evidgate.id': {
            type: 'string',
            format: 'uuid',
          },
          name: {
            type: 'string',
            minLength: 1,
            maxLength: 512,
          },
          requirement: {
            type: ['string', 'number', 'boolean', 'null'],
            maxLength: 128,
            pattern: '^[A-Za-z0-9._:/#-]+$',
            description:
              'Optional external requirement this file protects (D-A6): trimmed to 1..128 characters of A-Z a-z 0-9 . _ : / # -. A string is used as written and a number as its digits; anything else (a bare true/false) and any value outside that rule is a warning on this entry, and the case keeps a null reference rather than losing its id, so a typo here never freezes the directory.',
          },
        },
      },
    },
  },
};
