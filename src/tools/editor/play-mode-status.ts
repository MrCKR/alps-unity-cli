import { defineTool } from "../define.js";
import { readDurableStatus, type DurableStatusRequest, type DurableStatusResult } from "../status/support.js";

export default defineTool<DurableStatusRequest, DurableStatusResult>({
  name: "editor.play-mode-status",
  summary: "Read one editor.play-mode-start or editor.play-mode-stop root by requestId without changing Play Mode.",
  description: "Reads the active phase or exact terminal envelope for one editor.play-mode-start or editor.play-mode-stop root in the current Editor session. The original public requestId is the only business input; internal request digests are never exposed, and this Tool never changes Play Mode.",
  group: "editor", destructiveHint: false, target: "editor", playModeSupport: "supported", timeoutMs: 30_000,
  handler: "hybrid", supportStatus: "live-bridge",
  examples: [{ description: "Read a Play Mode transition after response loss", argv: ["tools", "run", "editor.play-mode-status", "--input", "{\"requestId\":\"play-mode-root-01\"}"] }],
  execute: (request, context) => readDurableStatus("internal.editor.play-mode.public-status", request, context),
});
