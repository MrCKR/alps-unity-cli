import os from "node:os";
import path from "node:path";

export function getRuntimePaths(env = process.env) {
  const base = env.ALPS_UNITY_HOME ?? path.join(os.homedir(), "AppData", "Local", "AlpsUnityCLI");
  return {
    root: base,
    configFile: path.join(base, "config.json"),
    instancesFile: path.join(base, "instances.json"),
    currentInstanceFile: path.join(base, "current-instance"),
    agentStateFile: path.join(base, "agent.json"),
    cliLog: path.join(base, "logs", "cli.log"),
    agentLog: path.join(base, "logs", "agent.log")
  };
}

export function normalizeFsPath(value) {
  return path.resolve(value).replace(/\\/g, "/").toLowerCase();
}
