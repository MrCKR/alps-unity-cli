import assert from "node:assert/strict";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { execute } from "../src/cli.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const offline = { dataDir: "Z:/definitely-missing-unity-cli-data" } as const;

test("tools group, search, and describe use static discovery without a live call", async () => {
  const group = await execute(["tools", "group"], offline);
  const search = await execute(["tools", "search", "hierarchy"], offline);
  const describe = await execute(["tools", "describe", "scene.hierarchy"], offline);

  assert.ok(group.ok);
  assert.ok(search.ok);
  assert.ok(describe.ok);
  assert.equal((describe.data as { name: string }).name, "scene.hierarchy");
  assert.ok((describe.data as { inputSchema?: unknown }).inputSchema);
  assert.ok((describe.data as { outputSchema?: unknown }).outputSchema);
});

test("TS-local Tool returns one validated direct business terminal", async () => {
  const result = await execute(["tools", "run", "docs.list", "--input", "{}"], offline);
  assert.ok(result.ok, JSON.stringify(result));
  assert.ok(Array.isArray((result.data as { topics?: unknown[] }).topics));
  assert.equal(Object.hasOwn(result.data as object, "command"), false);
  assert.equal(Object.hasOwn(result.data as object, "execution"), false);
});

test("Tool input must be a strict JSON object before dispatch", async () => {
  for (const input of ["[]", "true", "null", "{bad-json"]) {
    let calls = 0;
    const result = await execute(["tools", "run", "go.find", "--input", input], {
      ...offline,
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
    ...offline,
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
  assert.deepEqual(requests[0].input, { name: "go.find", input: { name: "Nothing", searchTerm: "Nothing" } });
  assert.equal((result.data as { matchCount: number }).matchCount, 0);
});

test("unknown tools and subcommands return stable errors", async () => {
  const missing = await execute(["tools", "run", "missing.tool", "--input", "{}"], offline);
  assert.ok(!missing.ok);
  assert.equal(missing.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");

  const subcommand = await execute(["tools", "unknown"], offline);
  assert.ok(!subcommand.ok);
  assert.equal(subcommand.error?.code, "UNKNOWN_TOOLS_COMMAND");
});
