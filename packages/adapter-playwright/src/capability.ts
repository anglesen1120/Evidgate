// 04.3.1-T1.3: the 00 §10 capability document this adapter declares. It is emitted as
// the first spooled envelope (a `run.log` envelope at sequence 1 carrying the document
// under `ext.capability`) and must validate against contracts/adapter/capability.v1.json.
// `supports` is extensible in the contract, but adding a flag is a contract change, not a
// local edit — this reporter declares the four required flags plus the one declared
// optional flag below.
//
// 04.3.3-T2: `supports.capture_control: false` is the honest declaration until this
// adapter honours contracts/adapter/config.v1.json. The contract
// (contracts/adapter/capability.v1.json, restated in contracts/adapter/README.md) reads an
// ABSENT `capture_control` as "the run-create configuration document is delivered and must
// be honoured", which this reporter does not do yet: it reads no capture flags, redaction
// patterns or per-kind size caps from the run config. Declaring `false` makes the server
// set the `capture_uncontrolled` label on the runs this adapter produces (T97), so silent
// non-conformance is visible on the run instead of implied (AC7). It is not a new schema
// member — `capture_control` is a declared boolean of the closed `supports.properties` — and
// the STATUS row "04.3.1-T1 capability supports.capture_control absent" names the receiver
// for honouring the configuration document.
export const PROTOCOL_MAJOR = 1;
export const PROTOCOL_MINOR = 0;
export const ENGINE_FAMILY = 'playwright';

export interface CapabilitySupports {
  discovery: boolean;
  step_events: boolean;
  retry: boolean;
  cancel: boolean;
  /**
   * 04.3.3-T2: `false` declares that this adapter cannot honour
   * `contracts/adapter/config.v1.json` yet, so the run carries the `capture_uncontrolled`
   * label (T97). See the file header.
   */
  capture_control: boolean;
  [flag: string]: boolean;
}

export interface CapabilityDocument {
  protocol: { major: number; minor: number };
  engine: { family: string; version: string };
  platforms: string[];
  supports: CapabilitySupports;
  mode: 'full' | 'ingest_only';
}

export interface CapabilityInput {
  /** The installed @playwright/test version (D8: detected at runtime, never pinned here). */
  engineVersion: string;
  /** `supports.step_events` — the reporter's `steps` option (AC6). */
  stepEvents: boolean;
  mode: 'full' | 'ingest_only';
  /** Defaults to the platform this reporter is running on. */
  platforms?: string[];
}

/** The platform token this reporter declares: `<process.platform>-<process.arch>`. */
export function currentPlatform(): string {
  return `${process.platform}-${process.arch}`;
}

/** Builds the capability document for one reporter instance. */
export function buildCapabilityDocument(input: CapabilityInput): CapabilityDocument {
  return {
    protocol: { major: PROTOCOL_MAJOR, minor: PROTOCOL_MINOR },
    engine: { family: ENGINE_FAMILY, version: input.engineVersion },
    platforms: input.platforms ?? [currentPlatform()],
    supports: {
      discovery: false,
      step_events: input.stepEvents,
      retry: true,
      cancel: false,
      // Always `false` until config.v1.json is honoured (see the file header).
      capture_control: false,
    },
    mode: input.mode,
  };
}
