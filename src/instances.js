import { getCurrentInstanceId, listRegisteredInstances, setCurrentInstanceId } from "./agent.js";
import { sameProject } from "./project.js";
import { SUPPORTED_BRIDGE_MAJOR } from "./protocol.js";
export { createInstanceId } from "./instance-id.js";

export async function selectInstance(options) {
  const instances = await listRegisteredInstances();
  const currentInstanceId = await getCurrentInstanceId();

  let candidates = instances;
  if (options.explicitInstance) {
    candidates = instances.filter((instance) => instance.id === options.explicitInstance);
  } else if (options.projectPath) {
    candidates = instances.filter((instance) => sameProject(instance.projectPath, options.projectPath));
  } else if (currentInstanceId) {
    candidates = instances.filter((instance) => instance.id === currentInstanceId);
  }

  if (candidates.length === 0) {
    return { kind: "none", instances };
  }

  if (options.explicitInstance && options.projectPath && !sameProject(candidates[0].projectPath, options.projectPath)) {
    return { kind: "mismatch", instance: candidates[0], projectPath: options.projectPath };
  }

  if (candidates.length > 1) {
    return { kind: "ambiguous", instances: candidates };
  }

  const instance = candidates[0];
  const major = Number.parseInt(instance.bridge.version.split(".")[0] ?? "0", 10);
  if (!instance.bridge.supported || major !== SUPPORTED_BRIDGE_MAJOR) {
    return { kind: "unsupportedBridge", instance };
  }

  return { kind: "selected", instance };
}

export async function useInstance(instanceId) {
  const instances = await listRegisteredInstances();
  const instance = instances.find((item) => item.id === instanceId);
  if (!instance) {
    return undefined;
  }
  await setCurrentInstanceId(instanceId);
  return instance;
}
