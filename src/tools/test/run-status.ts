import { defineTool } from "../define.js";
import { readDurableStatus, type DurableStatusRequest, type DurableStatusResult } from "../status/support.js";

export default defineTool<DurableStatusRequest, DurableStatusResult>({
  name: "test.run.status",
  summary: "Read one test.run root by requestId without starting another test run.",
  description: "Reads the active phase or exact terminal envelope for one test.run root in the current Editor session. The original public requestId is the only business input; internal request digests are never exposed, and this Tool never invokes the Test Runner.",
  group: "test", destructiveHint: false, target: "editor", playModeSupport: "supported", timeoutMs: 30_000,
  handler: "hybrid", supportStatus: "live-bridge",
  examples: [{ description: "Read a test run after response loss", argv: ["tools", "run", "test.run.status", "--input", "{\"requestId\":\"test-run-root-01\"}"] }],
  execute: (request, context) => readDurableStatus("internal.test.run.public-status", request, context),
});
