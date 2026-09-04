import { defineTool } from "../define.js";
import { readDurableStatus, type DurableStatusRequest, type DurableStatusResult } from "../status/support.js";

export default defineTool<DurableStatusRequest, DurableStatusResult>({
  name: "package.update.status",
  summary: "Read one package.update root by requestId without repeating file or UPM effects.",
  description: "Reads the active phase or exact terminal envelope for one package.update root in the current Editor session. The original public requestId is the only business input; internal request digests are never exposed, and this Tool never replays file mutation or Package Manager resolve.",
  group: "package", destructiveHint: false, target: "editor", playModeSupport: "supported", timeoutMs: 30_000,
  handler: "bridge", supportStatus: "live-bridge",
  examples: [{ description: "Read an update after response loss", argv: ["tools", "run", "package.update.status", "--input", "{\"requestId\":\"package-update-root-01\"}"] }],
  execute: (request, context) => readDurableStatus("internal.package.update.public-status", request, context),
});
