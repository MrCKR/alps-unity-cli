import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { POWERSHELL_UTF8_OUTPUT_LINE, withPowerShellUtf8Output } from "../src/powershell-transport.js";
import { buildClickDialogScript, type ObservedEditorDialog } from "../src/editor-dialogs.js";
import { WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE, buildWindowsEditorStateScript } from "../src/windows-editor-state.js";
import { WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT, WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT } from "../src/unity-project-processes.js";

/** 只在需要形状时使用的稳定 dialog fixture；不参与任何真实 effect。 */
function dialogFixture(): ObservedEditorDialog {
  return {
    dialogId: "dialog_fixture", title: "中文弹窗", message: "正文", modal: true, buttons: ["确定"],
    hwnd: "0x1", ownerHwnd: null, processId: 1234, processStartedAt: "2026-01-01T00:00:00.000Z",
    buttonTargets: [{ label: "确定", hwnd: "0x2", enabled: true }],
  };
}

test("PowerShell 输出编码契约是单一常量，并由 helper 放到脚本首行", () => {
  assert.equal(POWERSHELL_UTF8_OUTPUT_LINE, "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)");
  assert.equal(withPowerShellUtf8Output("Write-Output 'x'"), `${POWERSHELL_UTF8_OUTPUT_LINE}\nWrite-Output 'x'`);
});

test("每个导出的 PowerShell 脚本都以 UTF-8 输出契约为首行", () => {
  const scripts: Record<string, string> = {
    editorStateTemplate: WINDOWS_EDITOR_STATE_PS_SCRIPT_TEMPLATE,
    editorStateScript: buildWindowsEditorStateScript(1234),
    clickScript: buildClickDialogScript(dialogFixture(), { label: "确定", hwnd: "0x2" }),
    processDiscovery: WINDOWS_UNITY_PROCESS_DISCOVERY_SCRIPT,
    processClose: WINDOWS_UNITY_PROCESS_CLOSE_SCRIPT,
  };
  for (const [name, script] of Object.entries(scripts)) {
    assert.equal(script.split(/\r?\n/)[0]?.trim(), POWERSHELL_UTF8_OUTPUT_LINE, `${name} 的首行必须是 UTF-8 输出契约`);
  }
});

test("非导出 PowerShell 调用点复用共享契约，且不内联重复", () => {
  for (const file of ["src/instances.ts", "src/package-manager.ts", "src/cli-commands/skill-installer.ts", "src/tools/package/live-resolve.ts", "src/project-identity.ts"]) {
    const source = readFileSync(join(process.cwd(), file), "utf8");
    assert.match(source, /POWERSHELL_UTF8_OUTPUT_LINE|withPowerShellUtf8Output/, `${file} 必须接入共享 UTF-8 输出契约`);
    assert.doesNotMatch(source, /\[Text\.UTF8Encoding\]::new\(\$false\)/, `${file} 不得内联重复该契约`);
  }
});

test("中文标题与按钮名经 click 脚本传递时保持原始 Unicode", () => {
  const script = buildClickDialogScript(dialogFixture(), { label: "确定", hwnd: "0x2" });
  const titleBase64 = Buffer.from("中文弹窗", "utf8").toString("base64");
  const buttonBase64 = Buffer.from("确定", "utf8").toString("base64");
  assert.ok(script.includes(JSON.stringify(titleBase64)));
  assert.ok(script.includes(JSON.stringify(buttonBase64)));
  assert.equal(Buffer.from(titleBase64, "base64").toString("utf8"), "中文弹窗");
  assert.equal(Buffer.from(buttonBase64, "base64").toString("utf8"), "确定");
});
