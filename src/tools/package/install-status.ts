import { defineTool } from "../define.js";
import { readDurableStatus, type DurableStatusRequest, type DurableStatusResult } from "../status/support.js";

export default defineTool<DurableStatusRequest, DurableStatusResult>({
  name: "package.install-status",
  summary: "Read one package.install root by requestId without repeating file or UPM effects.",
  description: "Reads the active phase or exact terminal envelope for one package.install root in the current Editor session. The original public requestId is the only business input; internal request digests are never exposed, and this Tool never replays file mutation or Package Manager resolve.",
  group: "package", destructiveHint: false, target: "editor", playModeSupport: "supported", timeoutMs: 30_000,
  handler: "hybrid", supportStatus: "live-bridge",
  examples: [{ description: "Read an install after response loss", argv: ["tools", "run", "package.install-status", "--input", "{\"requestId\":\"package-install-root-01\"}"] }],
  execute: (request, context) => readDurableStatus("internal.package.install.public-status", request, context),
});
