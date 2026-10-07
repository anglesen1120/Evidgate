// 04.3.1-T1.3: the 00 §10 capability document this adapter declares. It is emitted as
// the first spooled envelope (a `run.log` envelope at sequence 1 carrying the document
// under `ext.capability`) and must validate against contracts/adapter/capability.v1.json.
// `supports` is extensible in the contract, but adding a flag is a contract change, not a
// local edit — this reporter declares only the four required flags.
export const PROTOCOL_MAJOR = 1;
export const PROTOCOL_MINOR = 0;
export const ENGINE_FAMILY = 'playwright';

export interface CapabilitySupports {
  discovery: boolean;
  step_events: boolean;
  retry: boolean;
  cancel: boolean;
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
    },
    mode: input.mode,
  };
}
