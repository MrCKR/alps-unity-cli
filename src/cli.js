#!/usr/bin/env node
import { ensureLocalAgent, postBridgeHeartbeat, startAgentServer } from "./agent.js";
import { statusCommand, projectInfoCommand, instanceCurrentCommand, instanceListCommand, instanceUseCommand, liveQueryCommand } from "./commands.js";
import { createInstanceId } from "./instances.js";
import { createRequestId, failure, success } from "./request.js";

function parseArgs(argv) {
  const positional = [];
  const flags = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token.startsWith("--")) {
      const key = token.slice(2);
      const next = argv[index + 1];
      if (next && !next.startsWith("--")) {
        flags[key] = next;
        index += 1;
      } else {
        flags[key] = true;
      }
    } else {
      positional.push(token);
    }
  }
  return { positional, flags };
}

async function main() {
  const requestId = createRequestId();
  const { positional, flags } = parseArgs(process.argv.slice(2));
  const options = {
    project: typeof flags.project === "string" ? flags.project : undefined,
    unity: typeof flags.unity === "string" ? flags.unity : undefined,
    instance: typeof flags.instance === "string" ? flags.instance : undefined
  };

  const [command, subcommand, argument] = positional;
  let result;

  if (!command || command === "status") {
    result = await statusCommand(requestId, options);
  } else if (command === "project" && subcommand === "info") {
    result = await projectInfoCommand(requestId, options);
  } else if (command === "instance" && subcommand === "list") {
    result = await instanceListCommand(requestId);
  } else if (command === "instance" && subcommand === "current") {
    result = await instanceCurrentCommand(requestId);
  } else if (command === "instance" && subcommand === "use") {
    result = await instanceUseCommand(requestId, argument);
  } else if (command === "scene" && ["active", "hierarchy", "build-settings"].includes(subcommand)) {
    result = await liveQueryCommand(requestId, `scene.${subcommand}`, queryArgs(flags), options);
  } else if (command === "go" && subcommand === "find") {
    result = await liveQueryCommand(requestId, "go.find", targetArgs(flags), options);
  } else if (command === "go" && subcommand === "inspect") {
    result = await liveQueryCommand(requestId, "go.inspect", targetArgs(flags, argument), options);
  } else if (command === "asset" && subcommand === "search") {
    result = await liveQueryCommand(requestId, "asset.search", assetArgs(flags, argument), options);
  } else if (command === "asset" && subcommand === "info") {
    result = await liveQueryCommand(requestId, "asset.info", assetArgs(flags, argument), options);
  } else if (command === "editor" && subcommand === "console") {
    result = await liveQueryCommand(requestId, "editor.console", consoleArgs(flags), options);
  } else if (command === "bridge" && subcommand === "register") {
    result = await bridgeRegisterCommand(requestId, flags);
  } else if (command === "agent" && subcommand === "start") {
    result = await agentStartCommand(requestId, flags);
  } else {
    result = failure({
      requestId,
      code: "E_CONNECTION_FAILED",
      message: `Unknown command: ${positional.join(" ")}`,
      nextActions: [{ action: "showHelp", description: "Use status, project info, instance list/current/use, scene, go, asset, editor console, bridge register, or agent start." }]
    });
  }

  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`, () => {
    process.exit(result.ok ? 0 : 1);
  });
}

function queryArgs(flags) {
  return {
    pageSize: numberFlag(flags, "page-size") ?? numberFlag(flags, "limit"),
    cursor: stringFlag(flags, "cursor"),
    filter: stringFlag(flags, "filter")
  };
}

function targetArgs(flags, positionalTarget) {
  return {
    ...queryArgs(flags),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    name: stringFlag(flags, "name"),
    tag: stringFlag(flags, "tag"),
    layer: stringFlag(flags, "layer"),
    component: stringFlag(flags, "component"),
    path: stringFlag(flags, "path"),
    id: stringFlag(flags, "id")
  };
}

function assetArgs(flags, positionalTarget) {
  return {
    ...queryArgs(flags),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "query"),
    query: stringFlag(flags, "query") ?? stringFlag(flags, "filter") ?? positionalTarget,
    type: stringFlag(flags, "type"),
    path: stringFlag(flags, "path"),
    guid: stringFlag(flags, "guid")
  };
}

function consoleArgs(flags) {
  return {
    ...queryArgs(flags),
    type: stringFlag(flags, "type"),
    count: numberFlag(flags, "count") ?? numberFlag(flags, "limit"),
    clear: flags.clear === true || flags.clear === "true"
  };
}

function stringFlag(flags, key) {
  return typeof flags[key] === "string" ? flags[key] : undefined;
}

function numberFlag(flags, key) {
  if (typeof flags[key] !== "string") {
    return undefined;
  }
  const value = Number.parseInt(flags[key], 10);
  return Number.isFinite(value) ? value : undefined;
}

async function bridgeRegisterCommand(requestId, flags) {
  const projectPath = typeof flags.project === "string" ? flags.project : undefined;
  if (!projectPath) {
    return failure({
      requestId,
      code: "E_PROJECT_NOT_FOUND",
      message: "Bridge registration requires --project.",
      nextActions: [{ action: "passProject", command: "alps-unity bridge register --project <path>", description: "Pass the Unity project path from the Editor Bridge." }]
    });
  }

  const instance = {
    id: typeof flags.id === "string" ? flags.id : createInstanceId(projectPath),
    projectPath,
    projectName: projectPath.split(/[\\/]/).filter(Boolean).at(-1) ?? "UnityProject",
    unityVersion: typeof flags.unityVersion === "string" ? flags.unityVersion : "unknown",
    editorPid: typeof flags.pid === "string" ? Number(flags.pid) : undefined,
    bridge: {
      version: typeof flags.bridgeVersion === "string" ? flags.bridgeVersion : "1.0.0",
      supported: flags.bridgeSupported !== "false",
      logPath: typeof flags.bridgeLog === "string" ? flags.bridgeLog : `${projectPath}/Logs/AlpsUnityBridge.log`
    },
    busy: {
      isBusy: flags.busy === true || flags.busy === "true",
      compiling: flags.compiling === true || flags.compiling === "true",
      importing: flags.importing === true || flags.importing === "true",
      building: flags.building === true || flags.building === "true",
      playModeChanging: flags.playModeChanging === true || flags.playModeChanging === "true",
      domainReloading: flags.domainReloading === true || flags.domainReloading === "true",
      prefabStage: flags.prefabStage === true || flags.prefabStage === "true"
    },
    connection: {
      transport: "http",
      endpoint: typeof flags.endpoint === "string" ? flags.endpoint : "http://127.0.0.1:47392"
    },
    lastHeartbeatAt: new Date().toISOString()
  };

  const endpoint = typeof flags.agentEndpoint === "string" ? flags.agentEndpoint : undefined;
  const agent = await ensureLocalAgent({ endpoint });
  const registered = await postBridgeHeartbeat(agent.endpoint, {
    id: instance.id,
    projectPath: instance.projectPath,
    projectName: instance.projectName,
    unityVersion: instance.unityVersion,
    editorPid: instance.editorPid,
    bridgeVersion: instance.bridge.version,
    bridgeSupported: instance.bridge.supported,
    bridgeLog: instance.bridge.logPath,
    busy: instance.busy.isBusy,
    compiling: instance.busy.compiling,
    importing: instance.busy.importing,
    building: instance.busy.building,
    playModeChanging: instance.busy.playModeChanging,
    domainReloading: instance.busy.domainReloading,
    prefabStage: instance.busy.prefabStage,
    transport: instance.connection.transport,
    endpoint: instance.connection.endpoint
  });
  return success({ requestId, mode: "agent", bridgeLog: registered.bridge.logPath, data: { agent, instance: registered }, nextActions: [] });
}

async function agentStartCommand(requestId, flags) {
  const endpoint = typeof flags.endpoint === "string" ? flags.endpoint : undefined;
  const agent = await ensureLocalAgent({ endpoint });
  return success({ requestId, mode: "agent", data: { agent }, nextActions: [] });
}

main().catch((error) => {
  const requestId = createRequestId();
  const result = failure({
    requestId,
    code: "E_CONNECTION_FAILED",
    message: error instanceof Error ? error.message : "Unexpected CLI failure.",
    details: { stack: error instanceof Error ? error.stack : undefined },
    nextActions: [{ action: "viewLogs", description: "Check CLI logs and retry the command." }]
  });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`, () => {
    process.exit(1);
  });
});
