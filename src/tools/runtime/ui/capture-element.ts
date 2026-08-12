import { defineTool } from "../../define.js";

export interface RuntimeCaptureElementRequest { selector: string; panel?: string }
export interface RuntimeCaptureElementInfo { name: string; type: string; width: number; height: number }
export interface RuntimeCaptureArtifact { path: string; size: number; sha256: string; mimeType: "image/png" }
export interface RuntimeCaptureElementResult {
  runtimeSessionId: string;
  dryRun: boolean;
  captured: boolean;
  panel: string;
  selector: string;
  element: RuntimeCaptureElementInfo;
  artifact?: RuntimeCaptureArtifact;
  plannedEffects: Array<{ type: "runtime-element-capture"; artifactName: "runtime-element.png" }>;
}

export default defineTool<RuntimeCaptureElementRequest, RuntimeCaptureElementResult>({
  name: "runtime.ui.capture-element",
  summary: "Capture one bounded UI Toolkit element from a development Player.",
  description: "Resolve one bounded selector in one live runtime UIDocument panel and write a PNG to the request artifact directory. The Tool is catalog-visible on supported projects but executes only on Unity 6000.7 or newer; it never returns base64 or accepts an output path.",
  group: "runtime",
  handler: "bridge-runtime",
  target: "runtime",
  destructiveHint: false,
  sideEffects: ["artifact-write"],
  supportsDryRun: true,
  playModeSupport: "runtime-only",
  timeoutMs: 15_000,
  examples: [{ description: "Capture the uniquely named runtime status label.", argv: ["tools", "run", "runtime.ui.capture-element", "--input", "{\"selector\":\"#status-label\"}"], input: { selector: "#status-label" } }],
  execute() { throw new Error("runtime.ui.capture-element must execute through the bridge-runtime dispatcher"); },
});
