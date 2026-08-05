export const PROBUILDER_REQUEST_SPECIFIC_COMPLETION = new Map([
  ["probuilder.append-vertices-to-edge", "request-edge-chain-readback"],
  ["probuilder.auto-smooth", "request-smoothing-compatibility-readback"],
  ["probuilder.bevel-edges", "request-bevel-rail-readback"],
  ["probuilder.bridge-edges", "request-bridge-face-readback"],
  ["probuilder.connect-elements", "request-connect-topology-readback"],
  ["probuilder.create-shape", "request-shape-topology-record-readback"],
  ["probuilder.duplicate-and-flip", "request-duplicate-flipped-face-readback"],
  ["probuilder.extrude-edges", "request-edge-extrusion-readback"],
  ["probuilder.extrude-faces", "request-face-extrusion-readback"],
  ["probuilder.flip-normals", "request-face-winding-readback"],
  ["probuilder.insert-vertex", "request-insert-vertex-readback"],
  ["probuilder.merge-objects", "request-world-triangle-material-union-readback"],
  ["probuilder.merge-vertices", "request-shared-vertex-remap-readback"],
  ["probuilder.subdivide", "request-face-child-union-readback"],
  ["probuilder.weld-vertices", "request-weld-cluster-remap-readback"],
]);

/** 验证 ProBuilder request-specific completion 闭集，禁止普通 Tool 扩张为 strict proof。 */
export function validateProBuilderCompletionScope(entries) {
  const failures = [];
  const probuilder = entries.filter((entry) => typeof entry?.toolName === "string" && entry.toolName.startsWith("probuilder."));
  const byName = new Map(probuilder.map((entry) => [entry.toolName, entry]));

  for (const [toolName, predicate] of PROBUILDER_REQUEST_SPECIFIC_COMPLETION) {
    const entry = byName.get(toolName);
    if (!entry) {
      failures.push(`${toolName}: approved request-specific completion contract is missing`);
      continue;
    }
    if (entry.executionClass !== "observed-state") failures.push(`${toolName}: executionClass must be observed-state`);
    if (entry.completionPredicate?.kind !== predicate) failures.push(`${toolName}: completionPredicate.kind must be ${predicate}`);
    if (entry.proofSource?.kind !== "fresh-request-specific-readback") failures.push(`${toolName}: proofSource.kind must be fresh-request-specific-readback`);
  }

  for (const entry of probuilder) {
    if (PROBUILDER_REQUEST_SPECIFIC_COMPLETION.has(entry.toolName)) continue;
    if (entry.executionClass === "observed-state" || entry.proofSource?.kind === "fresh-request-specific-readback") {
      failures.push(`${entry.toolName}: request-specific completion is outside the approved fifteen-tool scope`);
    }
  }

  return failures;
}
