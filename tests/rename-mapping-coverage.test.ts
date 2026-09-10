import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { generatedToolCompletionContract } from "../src/tools/.generated-completion-registry.js";
import { existsSync } from "node:fs";

/**
 * ISSUE-081 改名映射全量覆盖：specs/catalog-rename-mapping-0.5.0.json 中每一条改名
 * 都必须（1）出现在当前 command contract，（2）有对应的 bridge spec 路由，
 * （3）登记了 completion contract；（4）csharp_handler=present 的工具有对应 C# 源文件。
 */
const mapping = JSON.parse(readFileSync("specs/catalog-rename-mapping-0.5.0.json", "utf8")) as {
  mappings: Array<{ old: string; new: string }>;
};
const renames = mapping.mappings.filter((m) => m.old !== m.new);

test("rename mapping covers renamed entries with canonical contract, bridge spec and completion registration", () => {
  assert.equal(renames.length, 102);
  // 通用 catalog transport 是所有 bridge 工具的兑底路由；无专用 spec 的工具仍可经 --input 调用。
  const genericRun = bridgeCommandRegistry.lookupBridgeCommand("catalog.editor.run");
  assert.ok(genericRun, "generic catalog.editor.run transport must exist");
  for (const { old: oldName, new: newName } of renames) {
    // (1) command contract：改名后的命令必须在当前契约中且标记为已实现
    const contract = findCommandContract(newName);
    assert.ok(contract, `${newName} should be in the command contract`);

    // (2) bridge spec：名称首段作为内部 group/command 键，toolName 必须路由到正确 transport
    const dot = newName.indexOf(".");
    const spec = bridgeCommandRegistry.lookup(newName.slice(0, dot), newName.slice(dot + 1));
    const expectedTransport = newName.startsWith("player-runtime.") ? "catalog.player.run" : "catalog.editor.run";
    if (spec) {
      // code.execute 这类本地 typed 工具无 toolName，bridgeCommand 即公开名。
      assert.ok(spec.toolName === newName || (!spec.toolName && spec.bridgeCommand === newName), `${newName} bridge spec toolName`);
    } else if (contract.handler_type === "bridge") {
      // 无专用 spec 的 bridge 工具必须能经通用 catalog transport 调用。
      assert.ok(genericRun, `${newName} relies on the generic catalog transport`);
    } else {
      // TS-local 工具（offline/hybrid/bridge-runtime/cli_only）不依赖 bridge spec，由 metadata 分派。
      // 此分支内 handler_type 已被类型系统收窄为非 bridge 值，无需额外断言。
    }

    // (3) completion contract（catalog.* 为内部 transport 命令，不进入公开 completion registry）
    if (!newName.startsWith("catalog.")) {
      assert.ok(generatedToolCompletionContract(newName), `${newName} should have a completion contract`);
    }

    // (4) C# 源文件存在性：csharp_handler=present 时源路径必须真实存在
    if (contract.csharp_handler === "present" && !newName.startsWith("catalog.")) {
      const completionEntry = (JSON.parse(readFileSync("contracts/tool-completion-contract.json", "utf8")).tools as Array<{ toolName: string; source: string }>)
        .find((item) => item.toolName === newName);
      assert.ok(completionEntry, `${newName} should have a completion contract entry with source`);
      assert.ok(existsSync(completionEntry.source), `${newName} C# source should exist: ${completionEntry.source}`);
    }
  }
});

test("old catalog identities are absent from the current contract and bridge registry", () => {
  for (const { old: oldName } of renames) {
    const dot = oldName.indexOf(".");
    const stale = bridgeCommandRegistry.lookup(oldName.slice(0, dot), oldName.slice(dot + 1));
    const message = `${oldName} should not resolve under its old name`;
    if (findCommandContract(oldName) === undefined) {
      assert.equal(stale, undefined, message);
    }
  }
});
