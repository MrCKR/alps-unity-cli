import { AgentUnavailableError, ensureLocalAgent, getCurrentInstanceId, listRegisteredInstances } from "./agent.js";
import { selectInstance, useInstance } from "./instances.js";
import { getRuntimePaths } from "./paths.js";
import { discoverProject, readConfig } from "./project.js";
import { failure, success } from "./request.js";
import { discoverUnityExecutable } from "./unity.js";

export async function statusCommand(requestId, options = {}) {
  const runtimePaths = getRuntimePaths();
  const config = await readConfig(runtimePaths.configFile);
  const project = await discoverProject({ explicitProject: options.project, cwd: options.cwd ?? process.cwd(), config });
  if (!project) {
    return failure({
      requestId,
      code: "E_PROJECT_NOT_FOUND",
      message: "Unity project was not found from --project, current directory, default project, or recent projects.",
      nextActions: [
        { action: "setProject", command: "alps-unity status --project <path>", description: "Pass an explicit Unity project path." },
        { action: "openUnityProject", description: "Open or initialize a Unity project that contains Assets, Packages, and ProjectSettings." }
      ]
    });
  }

  const unity = await discoverUnityExecutable({ explicitUnity: options.unity, config });
  const agentResult = await ensureAgentOrFailure(requestId, { endpoint: config.agentEndpoint });
  if (!agentResult.ok) {
    return agentResult;
  }
  const agent = agentResult.agent;
  const selection = await selectInstance({ explicitInstance: options.instance, projectPath: project.path });

  if (selection.kind === "none") {
    return failure({
      requestId,
      code: "E_INSTANCE_NOT_FOUND",
      message: "No live Unity Editor Bridge instance is registered for the selected project.",
      details: { projectPath: project.path, knownInstances: selection.instances.map((instance) => instance.id) },
      nextActions: [
        { action: "openUnity", description: "Open the Unity project so the Alps Unity Editor Bridge can register a live instance." },
        { action: "initBridge", command: "alps-unity init --project <path>", description: "Install or refresh the Alps Unity Editor Package for this project." },
        { action: "viewLogs", description: "Check CLI and Local Agent logs for connection diagnostics." }
      ]
    });
  }

  if (selection.kind === "ambiguous") {
    return failure({
      requestId,
      code: "E_INSTANCE_AMBIGUOUS",
      message: "Multiple Unity Editor instances match the request.",
      details: { instances: selection.instances.map((instance) => ({ id: instance.id, projectPath: instance.projectPath })) },
      nextActions: [
        { action: "selectInstance", command: "alps-unity instance use <instanceId>", description: "Select the current instance explicitly." },
        { action: "passInstance", command: "alps-unity status --instance <instanceId>", description: "Pass --instance for this command." }
      ]
    });
  }

  if (selection.kind === "mismatch") {
    return failure({
      requestId,
      code: "E_INSTANCE_PROJECT_MISMATCH",
      message: "The selected instance belongs to a different Unity project.",
      details: { instanceProjectPath: selection.instance.projectPath, requestedProjectPath: selection.projectPath },
      nextActions: [
        { action: "chooseMatchingInstance", command: "alps-unity instance list", description: "List instances and choose one that matches the requested project." }
      ],
      bridgeLog: selection.instance.bridge.logPath
    });
  }

  if (selection.kind === "unsupportedBridge") {
    return failure({
      requestId,
      code: "E_BRIDGE_VERSION_UNSUPPORTED",
      message: "The registered Unity Editor Bridge version is not supported by this CLI.",
      details: { bridgeVersion: selection.instance.bridge.version, instanceId: selection.instance.id },
      nextActions: [
        { action: "updateBridge", command: "alps-unity init --project <path>", description: "Refresh the Alps Unity Editor Package to a supported version." }
      ],
      bridgeLog: selection.instance.bridge.logPath
    });
  }

  return success({
    requestId,
    mode: "live",
    bridgeLog: selection.instance.bridge.logPath,
    nextActions: selection.instance.busy.isBusy
      ? [{ action: "waitForUnity", description: "Unity is busy; wait for compiling, importing, build, PlayMode switch, domain reload, or Prefab Stage to complete." }]
      : [],
    data: {
      project,
      unity,
      agent,
      instance: selection.instance,
      mode: "live"
    }
  });
}

export async function projectInfoCommand(requestId, options = {}) {
  const runtimePaths = getRuntimePaths();
  const config = await readConfig(runtimePaths.configFile);
  const project = await discoverProject({ explicitProject: options.project, cwd: options.cwd ?? process.cwd(), config });
  if (!project) {
    return failure({
      requestId,
      code: "E_PROJECT_NOT_FOUND",
      message: "Unity project was not found.",
      nextActions: [
        { action: "setProject", command: "alps-unity project info --project <path>", description: "Pass an explicit Unity project path." }
      ]
    });
  }
  const unity = await discoverUnityExecutable({ explicitUnity: options.unity, config });
  return success({ requestId, mode: "offline", data: { project, unity }, nextActions: [] });
}

export async function instanceListCommand(requestId) {
  const agentResult = await ensureAgentOrFailure(requestId);
  if (!agentResult.ok) {
    return agentResult;
  }
  const instances = await listRegisteredInstances();
  const currentInstanceId = await getCurrentInstanceId();
  return success({ requestId, mode: "agent", data: { agent: agentResult.agent, currentInstanceId, instances }, nextActions: [] });
}

export async function instanceCurrentCommand(requestId) {
  const currentInstanceId = await getCurrentInstanceId();
  const instances = await listRegisteredInstances();
  const instance = currentInstanceId ? instances.find((item) => item.id === currentInstanceId) : undefined;
  if (!instance) {
    return failure({
      requestId,
      code: "E_INSTANCE_NOT_FOUND",
      message: "No current Unity instance is selected.",
      details: { currentInstanceId },
      nextActions: [
        { action: "selectInstance", command: "alps-unity instance list", description: "List registered instances and select one with instance use." }
      ]
    });
  }
  return success({ requestId, mode: "agent", bridgeLog: instance.bridge.logPath, data: { instance }, nextActions: [] });
}

export async function instanceUseCommand(requestId, instanceId) {
  if (!instanceId) {
    return failure({
      requestId,
      code: "E_INSTANCE_NOT_FOUND",
      message: "Missing instance id.",
      nextActions: [{ action: "listInstances", command: "alps-unity instance list", description: "Find an instance id to select." }]
    });
  }
  const instance = await useInstance(instanceId);
  if (!instance) {
    return failure({
      requestId,
      code: "E_INSTANCE_NOT_FOUND",
      message: "The requested Unity instance is not registered or heartbeat expired.",
      details: { instanceId },
      nextActions: [{ action: "listInstances", command: "alps-unity instance list", description: "List currently registered instances." }]
    });
  }
  return success({ requestId, mode: "agent", bridgeLog: instance.bridge.logPath, data: { currentInstanceId: instance.id, instance }, nextActions: [] });
}

async function ensureAgentOrFailure(requestId, options = {}) {
  try {
    return { ok: true, agent: await ensureLocalAgent(options) };
  } catch (error) {
    if (error instanceof AgentUnavailableError) {
      return failure({
        requestId,
        code: "E_AGENT_UNAVAILABLE",
        message: error.message,
        details: error.details,
        nextActions: [
          { action: "restartAgent", command: "alps-unity agent start", description: "Restart the Local Agent and retry the command." },
          { action: "viewLogs", description: "Check the CLI and Local Agent logs for startup or health-check failures." }
        ]
      });
    }
    throw error;
  }
}
