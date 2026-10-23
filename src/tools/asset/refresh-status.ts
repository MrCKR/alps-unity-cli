import { defineTool } from "../define.js";
import { readDurableStatus, type DurableStatusRequest, type DurableStatusResult } from "../status/support.js";

export default defineTool<DurableStatusRequest, DurableStatusResult>({
  name: "asset.refresh-status",
  summary: "Read one asset.refresh root by requestId without replaying its import.",
  description: "Reads the active phase or exact terminal envelope for one asset.refresh root in the current Editor session. The original public requestId is the only business input; internal request digests are never exposed, and this Tool never replays the import effect.",
  group: "asset", destructiveHint: false, target: "editor", playModeSupport: "supported", timeoutMs: 30_000,
  handler: "hybrid", supportStatus: "live-bridge",
  examples: [{ description: "Read an asset refresh after response loss", argv: ["tools", "run", "asset.refresh-status", "--input", "{\"requestId\":\"asset-refresh-root-01\"}"] }],
  execute: (request, context) => readDurableStatus("internal.asset.refresh.public-status", request, context),
});
