import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string): string => readFileSync(path, "utf8");
const support = read("unitypkg/Editor/Tools/project-settings/ProjectSettingsToolSupport.cs");
const reader = read("unitypkg/Editor/Tools/project-settings/ProjectSettingsReader.cs");
const readSources = [
  read("unitypkg/Editor/Tools/project-settings/audio-settings.cs"),
  read("unitypkg/Editor/Tools/project-settings/input-settings.cs"),
  read("unitypkg/Editor/Tools/project-settings/player-settings.cs"),
  read("unitypkg/Editor/Tools/project-settings/time-settings.cs"),
];
const setSources = [
  read("unitypkg/Editor/Tools/project-settings/audio-settings/set.cs"),
  read("unitypkg/Editor/Tools/project-settings/input-settings/set.cs"),
  read("unitypkg/Editor/Tools/project-settings/player-settings/set.cs"),
  read("unitypkg/Editor/Tools/project-settings/time-settings/set.cs"),
];
const names = [
  "project-settings.audio-settings",
  "project-settings.audio-settings.set",
  "project-settings.input-settings",
  "project-settings.input-settings.set",
  "project-settings.player-settings",
  "project-settings.player-settings.set",
  "project-settings.time-settings",
  "project-settings.time-settings.set",
];
const completion = JSON.parse(read("contracts/tool-completion-contract.json")) as { tools: Array<{ toolName: string }> };

test("Project Settings只公开批准的四读四写canonical Tool", () => {
  const source = readSources.join("\n") + setSources.join("\n");
  for (const name of names) {
    assert.match(source, new RegExp(`Name = "${name.replaceAll(".", "\\.")}"`));
    assert.equal(completion.tools.filter((entry) => entry.toolName === name).length, 1, `completion contract mismatch for ${name}`);
  }
  assert.doesNotMatch(source, /get_audio_settings|set_audio_settings|get_input_settings|set_input_settings|get_player_settings|set_player_settings|get_time_settings|set_time_settings/);
});

test("四个读取Tool有界且不进入mutation路径", () => {
  for (const source of readSources) {
    assert.match(source, /DestructiveHint = false/);
    assert.match(source, /SupportsDryRun = false/);
    assert.match(source, /SideEffects = System\.Array\.Empty<string>\(\)/);
    assert.doesNotMatch(source, /CommitEffect|SaveAssets|SetDirty|ApplyModifiedProperties/);
  }
  assert.match(readSources[1], /offset defaults to 0/);
  assert.match(readSources[1], /limit defaults to 50 with a maximum of 200/);
  assert.match(support, /DefaultInputLimit = 50/);
  assert.match(support, /MaxInputLimit = 200/);
  assert.match(support, /MaxInputAxisScan = 4096/);
  assert.match(support, /page\.Count < limit/);
  assert.match(support, /EnsureAxisCountBounded\(axes\)/);
  assert.doesNotMatch(reader, /CommitEffect|SaveAssets|SetAudio|SetInput|SetPlayer|SetTime/);
  for (const method of ["ReadAudio", "ReadInput", "ReadPlayer", "ReadTime"])
    assert.match(reader, new RegExp(`ProjectSettingsToolSupport\\.${method}\\(`));
});

test("四个写Tool只使用framework dry-run和单一effect barrier", () => {
  for (const source of setSources) {
    assert.match(source, /DestructiveHint = true/);
    assert.match(source, /SupportsDryRun = true/);
    assert.match(source, /"project-settings-write"/);
    assert.doesNotMatch(source, /confirm/i);
  }
  assert.equal((support.match(/ctx\.CommitEffect\(\);/g) ?? []).length, 4);
  assert.equal((support.match(/AssetDatabase\.SaveAssets\(\);/g) ?? []).length, 1, "one shared exact ProjectSettings persistence owner is required");
  assert.match(support, /if \(changedFields\.Count == 0 \|\| ctx\.DryRun\)/g);
  assert.match(support, /ApplyModifiedPropertiesWithoutUndo/);
  assert.match(support, /VerifyFileChanged/);
  assert.match(support, /SHA256\.Create\(\)/);
});

test("批准字段是typed闭集且不存在任意SerializedObject路由", () => {
  for (const field of [
    "Volume", "RolloffScale", "DopplerFactor",
    "Axis", "Sensitivity", "Gravity", "Dead",
    "CompanyName", "ProductName", "BundleVersion", "ScriptingBackend", "ApiCompatibilityLevel",
    "FixedDeltaTime", "MaximumDeltaTime", "TimeScale",
  ]) assert.match(support, new RegExp(`\\b${field}\\b`));
  assert.doesNotMatch(support, /Dictionary<string, object>|PropertyName\s*\{\s*get|Settings\s*\{\s*get;\s*set;\s*\}/);
  assert.match(support, /ProjectInputSettingsSetRequest[\s\S]*\[UnityCliRequired\][\s\S]*string Axis/);
  assert.match(setSources[1], /first configured entry is selected/);
  assert.match(support, /matches\[0\]/);
});

test("版本、enum与数值边界在effect前fail closed", () => {
  assert.match(support, /major == 2022 && minor == 3/);
  assert.match(support, /major == 2023/);
  assert.match(support, /major == 6000 && minor == 0/);
  assert.doesNotMatch(support, /major != 2022 && major != 2023 && major != 6000/);
  assert.match(support, /E_PROJECT_SETTINGS_VERSION_UNAVAILABLE/);
  assert.match(support, /Enum\.TryParse\(value, false/);
  assert.match(support, /Enum\.IsDefined/);
  assert.match(support, /double\.IsNaN/);
  assert.match(support, /maximumDeltaTime must be greater than or equal/);
  assert.match(support, /E_PROJECT_SETTINGS_INVALID/);
  assert.match(support, /E_PROJECT_SETTINGS_NO_FIELDS/);
  assert.match(support, /E_PROJECT_SETTINGS_LIMIT_EXCEEDED/);
  assert.match(support, /E_PROJECT_INPUT_AXIS_NOT_FOUND/);
  assert.match(support, /E_PROJECT_SETTINGS_TARGET_CHANGED/);
  assert.match(support, /E_PROJECT_SETTINGS_VERIFY_FAILED/);
  assert.match(support, /catch \(UnityCliBridge\.BridgeCommandException\)[\s\S]*throw;/);
});

test("Player reload风险和调用方fresh-read责任被明确投影", () => {
  const source = setSources[2];
  assert.match(source, /"domain-reload"/);
  assert.match(source, /may trigger a domain reload/);
  assert.match(source, /read project-settings\.player-settings before deciding whether to retry/);
  const entry = completion.tools.find((item) => item.toolName === "project-settings.player-settings.set") as Record<string, unknown> | undefined;
  assert.ok(entry);
  assert.equal(entry?.executionClass, "atomic");
  assert.match(JSON.stringify(entry), /project\.player-settings/);
});
