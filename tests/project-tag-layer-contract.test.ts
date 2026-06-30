import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile, readdir } from "node:fs/promises";

/** 递归列举 .cs 文件。 */
async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

/**
 * Project 域契约测试：project.tag / project.layer（Tag Manager 修改）。
 * 对齐 CoplayDev manage_editor add_tag/remove_tag/add_layer/remove_layer，
 * 验证 metadata/bridge/contract/C# 一致，且 bridge spec 透传结构化 --input 字段。
 */

test("project.tag and project.layer exist with correct safety hints", () => {
  const tag = findToolByName("project.tag")!;
  assert.equal(tag.destructiveHint, false);
  assert.equal(tag.handler, "bridge");
  assert.equal(tag.playModeSupport, "session-affecting");
  assert.ok(tag.sideEffects?.includes("project-settings-write"));

  const layer = findToolByName("project.layer")!;
  assert.equal(layer.playModeSupport, "session-affecting");
  assert.ok(layer.sideEffects?.includes("project-settings-write"));
});

test("project.tag/layer expose output schema with changed flag", () => {
  for (const name of ["project.tag", "project.layer"]) {
    const tool = findToolByName(name)!;
    const out = tool.outputSchema?.properties as Record<string, unknown>;
    assert.ok(out.changed, `${name} output should expose changed`);
    assert.ok(out.plannedEffects, `${name} output should expose plannedEffects`);
  }
});

test("bridge specs resolve for project tools", () => {
  const tag = bridgeCommandRegistry.lookup("project", "tag");
  assert.equal(tag?.bridgeCommand, "project.tag");
  const layer = bridgeCommandRegistry.lookup("project", "layer");
  assert.equal(layer?.bridgeCommand, "project.layer");
});

test("contract entries exist for project tools with present C# handler", () => {
  for (const name of ["project.tag", "project.layer"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for project.tag/layer", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["project.tag", "project.layer"]) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});

test("project bridge spec forwards structured --input fields (action/name/index)", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/editor.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "action"\)/, "bridge should forward structured action");
  assert.match(spec, /stringField\(flags, "name"\)/, "bridge should forward structured name");
  assert.match(spec, /numberField\(flags, "index"\)/, "bridge should forward structured index");
});
