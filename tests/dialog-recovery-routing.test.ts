import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { handleCommand } from "../src/cli-commands/command.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { resolveCatalogIndependentInvocationTool, resolveCatalogIndependentOperabilityTool } from "../src/tools/catalog.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { findToolByName } from "../src/tools/metadata/index.js";

const OPERABILITY_TOOLS = ["editor.state", "editor.dialogs", "editor.dialog.click"] as const;

test("only exact Editor operability tools own catalog-independent descriptors", () => {
  for (const name of OPERABILITY_TOOLS) {
    const resolved = resolveCatalogIndependentOperabilityTool(name);
    assert.deepEqual(resolved, findToolByName(name));
    assert.equal(resolved?.handler, "offline");
  }
  for (const name of ["dialogs", "editor.compile", "docs.get", "package.list"])
    assert.equal(resolveCatalogIndependentOperabilityTool(name), undefined, name);
});

test("tools run validates Editor operability execution without a live catalog", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dialog-recovery-routing-"));
  try {
    for (const name of OPERABILITY_TOOLS) {
      const result = await dispatchTool(name, undefined, { input: "{}", "dry-run": true }, { dataDir }, `req_${name.replaceAll(".", "_")}`);
      assert.equal(result.ok, false, name);
      if (!result.ok) {
        assert.equal(result.error.code, "E_DRY_RUN_UNSUPPORTED", name);
        assert.deepEqual(result.error.details, { toolName: name });
      }
    }
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("tools describe learns exact Editor operability contracts without a live catalog", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dialog-recovery-describe-"));
  try {
    for (const name of OPERABILITY_TOOLS) {
      assert.deepEqual(resolveCatalogIndependentInvocationTool("tools.describe", name), findToolByName("tools.describe"));
      const result = await dispatchTool("tools.describe", undefined, { input: "{}" }, { dataDir, projectTools: [] }, `req_describe_${name.replaceAll(".", "_")}`, { name });
      assert.equal(result.ok, true, JSON.stringify(result));
      if (result.ok) {
        const data = result.data as { name: string; inputSchema?: unknown };
        assert.equal(data.name, name);
        assert.ok(data.inputSchema);
      }
    }
    assert.equal(resolveCatalogIndependentInvocationTool("tools.describe", "scene.hierarchy"), undefined);
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});

test("command help and execution reuse Editor operability descriptors without a live catalog", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dialog-recovery-command-"));
  try {
    for (const name of OPERABILITY_TOOLS) {
      const help = await handleCommand([name], { help: true }, { dataDir }, `req_help_${name.replaceAll(".", "_")}`);
      assert.equal(help.ok, true, JSON.stringify(help));
      if (help.ok) {
        const data = help.data as { command: { name: string }; tool: { inputSchema?: unknown } };
        assert.equal(data.command.name, name);
        assert.ok(data.tool.inputSchema);
      }
      const businessTokens = name === "editor.dialog.click" ? ["--dialogId", "dialog_test", "--button", "Close"] : [];
      const execution = await handleCommand([name, ...businessTokens], { "dry-run": true }, { dataDir }, `req_command_${name.replaceAll(".", "_")}`);
      assert.equal(execution.ok, false, name);
      if (!execution.ok) assert.equal(execution.error.code, "E_DRY_RUN_UNSUPPORTED", name);
    }
  } finally {
    await rm(dataDir, { recursive: true, force: true });
  }
});
