import { randomUUID } from "node:crypto";
import { getRuntimePaths } from "./paths.js";

export function createRequestId() {
  return randomUUID();
}

export function success(params) {
  const runtimePaths = getRuntimePaths();
  return {
    ok: true,
    requestId: params.requestId,
    mode: params.mode,
    logs: {
      cli: runtimePaths.cliLog,
      agent: runtimePaths.agentLog,
      bridge: params.bridgeLog
    },
    nextActions: params.nextActions ?? [],
    data: params.data
  };
}

export function failure(params) {
  const runtimePaths = getRuntimePaths();
  return {
    ok: false,
    requestId: params.requestId,
    error: {
      code: params.code,
      message: params.message,
      details: params.details
    },
    logs: {
      cli: runtimePaths.cliLog,
      agent: runtimePaths.agentLog,
      bridge: params.bridgeLog
    },
    nextActions: params.nextActions
  };
}
