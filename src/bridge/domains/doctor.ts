import type { BridgeCommandSpec } from "../types.js";

const emptyArgs: BridgeCommandSpec["buildArgs"] = () => ({});

/** doctor 私有 reads 只为控制面聚合；不得进入 public Tool catalog。 */
export const doctorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "internal-doctor", command: "business", bridgeCommand: "internal.doctor.business", buildArgs: emptyArgs, internal: true },
  { group: "internal-doctor", command: "catalog", bridgeCommand: "internal.doctor.catalog", buildArgs: emptyArgs, internal: true },
  { group: "internal-doctor", command: "compile", bridgeCommand: "internal.doctor.compile", buildArgs: emptyArgs, internal: true },
];
