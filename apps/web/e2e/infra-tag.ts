// A test titled with this whole token (`@infrastructure` or `@infra-x` do not match) is browser or
// runner flake, never a product assertion, and is the only kind a Playwright config may retry.
export const INFRA_TAG = /@infra(?![\w-])/;

// Band below every OS ephemeral range (Linux 32768-60999, macOS and Windows 49152-65535), so an
// automatically allocated Docker or SSH port forward never lands on the webServer port.
export const E2E_PORT_MIN = 20_000;
export const E2E_PORT_MAX = 30_000;
