// The negative controls: each breaks one document the way a contract change would, so the checks
// above cannot pass while validating nothing. Every mutation is applied to a freshly read page, so
// no test sees another's edit.

import {
  corpusDocument,
  requiredMembers,
  requiredRunUuidField,
  runSummarySchema,
  runsListDocument,
  runsListSubject,
  undeclaredResponseHeaders,
} from './fixture-contract.corpus';
import {
  VALIDATOR,
  declaredResponseHeaderNames,
  describeErrors,
  isRecord,
} from './fixture-contract.support';

/** The `listRuns` operation the body controls mutate. */
const LIST_RUNS_OPERATION = 'listRuns';

/** A string the contract's `format: uuid` refuses, used by the second negative control. */
const NOT_A_UUID = 'not-a-uuid';

/** A header name no response of `contracts/openapi.yaml` declares, used by the third one. */
const UNDECLARED_HEADER = 'X-Fixture-Undeclared';

/** The policy revision document whose standalone body schema the last control breaks. */
const POLICY_REVISION_DOCUMENT_FIXTURE = 'policy-revision-head';

/** The 412 document whose envelope the last control answers under a success status. */
const VERSION_POINTER_DOCUMENT_FIXTURE = 'policy-revision-create-version-pointer';

/** A member only `contracts/schemas/policy-revision.v1.json` declares, typed `integer`. */
const POLICY_BODY_TYPED_MEMBER = 'flaky_budget';

/** A value that schema's `integer` type refuses. */
const NOT_A_NUMBER = 'not-a-number';

describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('fails on the runs-list page when a required run member is removed (AC10)', () => {
    const field = requiredMembers(runSummarySchema())[0];
    if (field === undefined) {
      throw new Error(
        '#/components/schemas/RunSummary declares no required member, so the negative control cannot remove one',
      );
    }

    const { page, row } = runsListSubject();
    const baseline = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      baseline.errors,
      `the unmutated runs-list page must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    expect(
      Object.keys(row),
      `the runs-list row does not carry the required member '${field}', so removing it proves nothing`,
    ).toContain(field);

    const withoutMember: Record<string, unknown> = {};
    for (const [member, value] of Object.entries(row)) {
      if (member !== field) {
        withoutMember[member] = value;
      }
    }
    page['items'] = [withoutMember];

    const result = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      result.errors.length,
      `a listRuns page whose run row lost the required member '${field}' still validated against contracts/openapi.yaml — the contract check cannot catch a schema drift (AC10)`,
    ).toBeGreaterThan(0);
    // Ajv locates a `required` failure on the object and names the missing member in `params`;
    // the shared mapper appends it to the path (`/items/0/<field>`). Either spelling names the
    // removed field, so both are accepted — the rows are printed when neither does.
    expect(
      result.errors.some((error) => error.path.includes(field) || error.reason === 'required'),
      `the contract check reported ${describeErrors(result.errors)}, none of which names the removed '${field}'`,
    ).toBe(true);
  });

  it('fails on a run row whose uuid member is not a uuid', () => {
    const field = requiredRunUuidField();
    const { page, row } = runsListSubject();
    const baseline = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      baseline.errors,
      `the unmutated runs-list page must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    page['items'] = [{ ...row, [field]: NOT_A_UUID }];

    const result = VALIDATOR.validateResponse(LIST_RUNS_OPERATION, 200, page);
    expect(
      result.errors.length,
      `a listRuns page whose run row carries '${field}: ${NOT_A_UUID}' still validated against contracts/openapi.yaml — the contract check ignores the uuid format (AC10)`,
    ).toBeGreaterThan(0);
    expect(
      result.errors.some((error) => error.path.includes(field) || error.reason === 'format'),
      `the contract check reported ${describeErrors(result.errors)}, none of which names '${field}'`,
    ).toBe(true);
  });

  it('reports a fixture header contracts/openapi.yaml does not declare', () => {
    const document = runsListDocument();
    const declared = declaredResponseHeaderNames(document.operation_id, document.status);

    // The response ref is followed before its `headers` object is read: a check that looked at
    // the operation's own `responses['200']` and stopped there would see no header at all.
    expect(
      declared.map((name) => name.toLowerCase()),
      `the ${document.status} response of '${document.operation_id}' declares no 'x-request-id', so the baseline below is empty for the wrong reason`,
    ).toContain('x-request-id');
    expect(
      undeclaredResponseHeaders(document),
      'the unmutated runs-list headers must be declared before the control adds one that is not',
    ).toEqual([]);

    // Header names are case-insensitive: a fixture answering the declared header in another
    // case is still declared, so only a genuinely absent name fails.
    const lowercasedHeaders: Record<string, string> = { 'x-request-id': 'fixture-request-id' };
    const lowercased = { ...document, headers: lowercasedHeaders };
    expect(
      undeclaredResponseHeaders(lowercased),
      "a lowercased 'x-request-id' must count as the declared 'X-Request-Id'",
    ).toEqual([]);

    const withUndeclared = { ...document, headers: { ...lowercasedHeaders } };
    withUndeclared.headers[UNDECLARED_HEADER] = 'fixture-only';
    const undeclared = undeclaredResponseHeaders(withUndeclared);
    expect(
      undeclared,
      `a runs-list fixture answering '${UNDECLARED_HEADER}' still passed the header check — it cannot catch a fixture that answers a header contracts/openapi.yaml does not declare`,
    ).toEqual([UNDECLARED_HEADER]);
  });

  // 09.7.1-T4: the control of the external-reference inlining. `PolicyRevisionDto.body` is the
  // standalone `contracts/schemas/policy-revision.v1.json`, and the validator reports
  // `schema_unavailable` for a response whose schema it cannot resolve — an unchecked policy page
  // behind a green suite. This breaks a member only that document declares and requires the row.
  it('enforces the standalone policy body schema contracts/openapi.yaml references', () => {
    const document = corpusDocument(POLICY_REVISION_DOCUMENT_FIXTURE);
    const baseline = VALIDATOR.validateResponse(
      document.operation_id,
      document.status,
      document.body,
    );
    expect(
      baseline.errors,
      `the unmutated policy revision document must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    const revisionBody = isRecord(document.body) ? document.body['body'] : undefined;
    expect(
      isRecord(revisionBody),
      'the policy revision document carries no object `body`, so the control below mutates nothing',
    ).toBe(true);

    const mutated = {
      ...(document.body as Record<string, unknown>),
      // `flaky_budget` is declared by contracts/schemas/policy-revision.v1.json alone, so a body
      // the validator accepted with it mistyped had its `body` schema skipped, not checked.
      body: {
        ...(revisionBody as Record<string, unknown>),
        [POLICY_BODY_TYPED_MEMBER]: NOT_A_NUMBER,
      },
    };

    const result = VALIDATOR.validateResponse(document.operation_id, document.status, mutated);
    expect(
      result.errors.length,
      `a policy revision whose \`body.${POLICY_BODY_TYPED_MEMBER}\` is '${NOT_A_NUMBER}' still validated — the contract's standalone policy body schema is not enforced`,
    ).toBeGreaterThan(0);
    expect(
      result.errors.some(
        (error) => error.path.includes(POLICY_BODY_TYPED_MEMBER) || error.reason === 'type',
      ),
      `the contract check reported ${describeErrors(result.errors)}, none of which names the mistyped '${POLICY_BODY_TYPED_MEMBER}'`,
    ).toBe(true);
  });

  // The pointer-form 412 exists so a browser case can prove the shared conflict dialog reads
  // `details[].path = /version`. Its envelope is only checked because the generic loop validates the
  // document at its own status; this control answers the same body under a success status and
  // requires the contract to refuse it, so the loop cannot be green while validating nothing.
  it('refuses the pointer-form 412 envelope under a success status', () => {
    const document = corpusDocument(VERSION_POINTER_DOCUMENT_FIXTURE);
    const baseline = VALIDATOR.validateResponse(
      document.operation_id,
      document.status,
      document.body,
    );
    expect(
      baseline.errors,
      `the unmutated pointer-form document must validate before it is broken: ${describeErrors(baseline.errors)}`,
    ).toEqual([]);

    const nowSuccess = VALIDATOR.validateResponse(document.operation_id, 200, document.body);
    expect(
      nowSuccess.errors.length,
      `a 412 error envelope validated as the contract's 200 answers no error, so the document's contract check proves nothing: ${describeErrors(nowSuccess.errors)}`,
    ).toBeGreaterThan(0);
  });
});
