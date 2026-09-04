import { defineTool } from "../define.js";
import { readDurableStatus, type DurableStatusRequest, type DurableStatusResult } from "../status/support.js";

export default defineTool<DurableStatusRequest, DurableStatusResult>({
  name: "build.player.status",
  summary: "Read one player build root by requestId without rebuilding.",
  description: "Reads the active phase or exact terminal envelope for one player build root in the current Editor session. The original public requestId is the only business input; internal request digests are never exposed, and this Tool never starts BuildPlayer.",
  group: "build", destructiveHint: false, target: "editor", playModeSupport: "supported", timeoutMs: 30_000,
  handler: "bridge", supportStatus: "live-bridge",
  examples: [{ description: "Read a player build after response loss", argv: ["tools", "run", "build.player.status", "--input", "{\"requestId\":\"build-player-root-01\"}"] }],
  execute: (request, context) => readDurableStatus("internal.build.player.public-status", request, context),
});
