#!/usr/bin/env node
import { startAgentServer } from "./agent.js";

const endpoint = process.env.ALPS_UNITY_AGENT_ENDPOINT ?? "http://127.0.0.1:47391";

startAgentServer(endpoint).catch((error) => {
  process.stderr.write(`Alps Unity Local Agent failed to start: ${error instanceof Error ? error.message : String(error)}\n`);
  process.exit(1);
});
