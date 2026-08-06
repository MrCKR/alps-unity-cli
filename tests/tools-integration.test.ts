import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";
import { staticBridgeToolMetadata } from "../src/tools/catalog.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const disconnected = { dataDir: "Z:/definitely-missing-unity-cli-data" } as const;
const connected = { ...disconnected, projectTools: staticBridgeToolMetadata(), runtimeTools: [] } as const;

test("tools group, search, and describe use one connected instance catalog", async () => {
  const group = await execute(["tools", "group"], connected as never);
  const search = await execute(["tools", "search", "hierarchy"], connected as never);
  const describe = await execute(["tools", "describe", "scene.hierarchy"], connected as never);

  assert.ok(group.ok);
  assert.ok(search.ok);
  assert.ok(describe.ok);
  assert.equal((describe.data as { name: string }).name, "scene.hierarchy");
  assert.ok((describe.data as { inputSchema?: unknown }).inputSchema);
  assert.ok((describe.data as { outputSchema?: unknown }).outputSchema);
});

test("target both keeps default editor availability when runtime is not ready", async () => {
  const editor = makeLiveTool({
    name: "sample.both",
    summary: "Read from either execution path.",
    description: "Read from the default editor path or an explicitly requested runtime path.",
    group: "sample",
    source: "project",
    target: "both",
    available: true,
  });
  const runtime = { ...editor, available: false, unavailableReason: "E_RUNTIME_NOT_READY: Enter Play Mode before executing this Tool." };
  const result = await execute(["tools", "describe", "sample.both"], {
    ...disconnected,
    projectTools: [editor],
    runtimeTools: [runtime],
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal((result.data as { available: boolean }).available, true);
  assert.equal(Object.hasOwn(result.data as object, "unavailableReason"), false);
});

test("tools run fails closed on a local and live ownership conflict", async () => {
  for (const name of ["tools.group", "code.exec"]) {
    const conflicting = makeLiveTool({
      name,
      summary: "Conflicting live owner.",
      description: "Conflicting live registration for a package-local Tool name.",
      group: name.split(".")[0],
      source: "project",
    });
    const result = await execute(["tools", "run", name, "--input", "{}"], {
      ...disconnected,
      projectTools: [conflicting],
      runtimeTools: [],
      bridgeExecute: async () => { throw new Error("ownership conflict must fail before Bridge dispatch"); },
    } as never);

    assert.equal(result.ok, false, name);
    if (!result.ok) assert.equal(result.error.code, "E_TOOL_SCHEMA_CONFLICT", name);
  }
});

test("TS-local Tool returns one validated direct business terminal", async () => {
  const result = await execute(["tools", "run", "tools.group", "--input", "{}"], connected as never);
  assert.ok(result.ok, JSON.stringify(result));
  assert.ok(Array.isArray((result.data as { groups?: unknown[] }).groups));
  assert.equal(Object.hasOwn(result.data as object, "command"), false);
  assert.equal(Object.hasOwn(result.data as object, "execution"), false);
});

test("Tool input must be a strict JSON object before dispatch", async () => {
  for (const input of ["[]", "true", "null", "{bad-json"]) {
    let calls = 0;
    const result = await execute(["tools", "run", "go.find", "--input", input], {
      ...connected,
      bridgeExecute: async () => { calls += 1; throw new Error("must not dispatch"); },
    } as never);
    assert.ok(!result.ok, input);
    assert.ok(["E_SCHEMA_INVALID", "E_TOOL_INPUT_JSON_INVALID"].includes(result.error?.code ?? ""), `${input}: ${result.error?.code}`);
    assert.equal(calls, 0, input);
  }
});

test("live built-in Tool routes once through project-tool.run", async () => {
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "go.find", "--input", "{\"name\":\"Nothing\"}"], {
    ...connected,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          result: {
            includeInactive: false,
            items: [],
            matchCount: 0,
            pageInfo: { cursor: 0, pageSize: 100, total: 0 },
            prefabStageOpen: false,
            preferPrefabStage: false,
            query: "Nothing",
            searchMethod: "by_name",
          },
        },
      };
    },
  } as never);

  assert.ok(result.ok, JSON.stringify(result));
  assert.equal(requests.length, 1);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input, { name: "go.find", input: { name: "Nothing" } });
  assert.equal((result.data as { matchCount: number }).matchCount, 0);
});

test("unknown tools and subcommands return stable errors", async () => {
  for (const name of ["missing.tool", "docs.list", "docs.topic"]) {
    const missing = await execute(["tools", "run", name, "--input", "{}"], connected as never);
    assert.ok(!missing.ok);
    assert.equal(missing.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
  }

  const subcommand = await execute(["tools", "unknown"], disconnected);
  assert.ok(!subcommand.ok);
  assert.equal(subcommand.error?.code, "UNKNOWN_TOOLS_COMMAND");
});
