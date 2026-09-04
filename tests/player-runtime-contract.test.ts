import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { findToolByName } from "../src/tools/metadata/index.js";

const RUNTIME_FILES = {
  asmdef: "unitypkg/Runtime/UnityCli.Runtime.asmdef",
  bridge: "unitypkg/Runtime/Infrastructure/UnityCliRuntimeBridge.cs",
  json: "unitypkg/Runtime/Infrastructure/UnityCliRuntimeJson.cs",
  registry: "unitypkg/Runtime/Infrastructure/UnityCliRuntimeRegistry.cs",
  injection: "unitypkg/Editor/Infrastructure/UnityCliRuntimeBuildInjection.cs",
} as const;

async function sources(): Promise<Record<keyof typeof RUNTIME_FILES, string>> {
  const entries = await Promise.all(Object.entries(RUNTIME_FILES).map(async ([key, path]) => [key, await readFile(path, "utf8")] as const));
  return Object.fromEntries(entries) as Record<keyof typeof RUNTIME_FILES, string>;
}

function assertBefore(source: string, first: string, second: string): void {
  const firstIndex = source.indexOf(first);
  assert.ok(firstIndex >= 0, `missing source anchor: ${first}`);
  const secondIndex = source.indexOf(second);
  assert.ok(secondIndex >= 0, `missing source anchor: ${second}`);
  assert.ok(firstIndex < secondIndex, `${first} must precede ${second}`);
}

test("development Player runtime assembly stays UnityEditor-independent and testable", async () => {
  const source = await sources();
  const asmdef = JSON.parse(source.asmdef) as { name: string; includePlatforms: string[]; references: string[] };
  assert.equal(asmdef.name, "UnityCli.Runtime");
  assert.deepEqual(asmdef.includePlatforms, ["Editor", "WindowsStandalone32", "WindowsStandalone64"]);
  assert.deepEqual(asmdef.references, ["UnityCli.Shared"]);
  for (const text of [source.bridge, source.json, source.registry]) {
    assert.doesNotMatch(text, /using UnityEditor|Newtonsoft\.|System\.Web\./);
  }
  assert.match(source.bridge, /Debug\.isDebugBuild/);
  assert.match(source.bridge, /UNITY_STANDALONE_WIN && !UNITY_EDITOR/);
});

test("post-build injection writes only the fixed development Player sidecar", async () => {
  const { injection } = await sources();
  assert.match(injection, /IPostprocessBuildWithReport/);
  assert.match(injection, /BuildOptions\.Development/);
  assert.match(injection, /StandaloneWindows64/);
  assert.match(injection, /executable \+ "_Data", "UnityCli", "runtime-config\.json"/);
  for (const field of ["instanceId", "canonicalProjectPath", "displayProjectPath", "buildId", "buildStamp", "developmentBuild"]) {
    assert.match(injection, new RegExp(`"${field}"`));
  }
  assert.match(injection, /UnityCliCanonicalProjectPath\.Resolve\(Application\.dataPath\)/);
  assert.doesNotMatch(injection, /AssetDatabase\.|Path\.Combine\([^\n]*"Assets"|Path\.Combine\([^\n]*"ProjectSettings"|Path\.Combine\([^\n]*"StreamingAssets"/);
  assert.match(injection, /path \+ "\.tmp"/);
});

test("runtime listener and registry freeze one loopback session identity", async () => {
  const { bridge, registry } = await sources();
  assert.match(bridge, /http:\/\/127\.0\.0\.1:/);
  assert.match(bridge, /RequestBodyLimitBytes = 1024 \* 1024/);
  assert.match(bridge, /"x-unity-cli-expected-target"/);
  assert.match(bridge, /"x-unity-cli-expected-session-id"/);
  assert.match(bridge, /"x-unity-cli-expected-listener-generation"/);
  assert.match(bridge, /"x-unity-cli-target"\] = "runtime"/);
  assert.match(bridge, /ReadObject\(request, "execution"\)/);
  assert.match(bridge, /absoluteDeadlineUtc/);
  assert.match(bridge, /runtime-tool\.list/);
  assert.doesNotMatch(bridge, /Application\.Quit|eval|HotReload/);

  assert.match(registry, /"Local\\\\UnityCli\.Registry\." \+ identity\.InstanceId/);
  assert.match(registry, /identity\.InstanceId \+ "\.json"/);
  assert.match(registry, /"target", "runtime"/);
  assert.match(registry, /"sessionId", sessionId/);
  assert.match(registry, /"runtimeSessionId", sessionId/);
  assert.match(registry, /"sourceRuntimeSessionId", sessionId/);
  assert.match(registry, /"progress", new List<object>\(\)/);
  assert.match(registry, /path \+ "\.tmp"/);
  assert.match(registry, /RemoveCurrentClaim\(\)/);
});

test("Runtime observation Tools are owned by the TS catalog and routed through one Player dispatcher", async () => {
  const names = ["runtime.console", "runtime.log", "runtime.state", "runtime.ui.capture-element"];
  for (const name of names) {
    const tool = findToolByName(name);
    assert.ok(tool, name);
    assert.equal(tool.handler, "bridge-runtime");
    assert.equal(tool.target, "runtime");
    assert.equal(tool.playModeSupport, "runtime-only");
    assert.equal(tool.inputSchema.type, "object");
    assert.equal(tool.outputSchema?.type, "object");
  }

  let calls = 0;
  const result = await dispatchTool("runtime.state", undefined, { input: "{}" }, {
    bridgeExecute: async (request) => {
      calls += 1;
      assert.equal(request.command, "runtime-tool.run");
      assert.deepEqual(request.input, { name: "runtime.state", input: {} });
      return { ok: true, requestId: request.requestId, data: { result: {
        runtimeSessionId: `session-${"a".repeat(64)}`, buildId: "b".repeat(32), observedAt: new Date().toISOString(),
        unityVersion: "2022.3.61f1", platform: "WindowsPlayer", productName: "fixture", applicationVersion: "1.0",
        scene: { name: "RuntimeFixture", buildIndex: 0, isLoaded: true },
        frame: { frameCount: 1, realtimeSinceStartup: 1, targetFrameRate: -1 },
        time: { timeScale: 1, time: 1, unscaledTime: 1 },
        screen: { width: 640, height: 360, dpi: 96, fullScreen: false, focused: true },
        memory: { totalAllocatedBytes: 1, totalReservedBytes: 1, totalUnusedReservedBytes: 0 },
      } } };
    },
  }, "req_runtime_state");
  assert.equal(result.ok, true);
  assert.equal(calls, 1);
});

test("Runtime controls stay session-only and package-gated", async () => {
  const names = ["runtime.input.key", "runtime.input.pointer", "runtime.target-framerate.set", "runtime.time-scale.set"];
  for (const name of names) {
    const tool = findToolByName(name);
    assert.ok(tool, name);
    assert.equal(tool.handler, "bridge-runtime");
    assert.equal(tool.target, "runtime");
    assert.equal(tool.supportsDryRun, true);
    assert.deepEqual(tool.sideEffects, ["runtime-session"]);
  }
  assert.deepEqual(findToolByName("runtime.input.key")?.requiresPackages, ["com.unity.inputsystem"]);
  assert.deepEqual(findToolByName("runtime.input.pointer")?.requiresPackages, ["com.unity.inputsystem"]);

  const tools = await readFile("unitypkg/Runtime/Tools/UnityCliRuntimeTools.cs", "utf8");
  assert.match(tools, /case "runtime\.target-framerate\.set"/);
  assert.match(tools, /case "runtime\.time-scale\.set"/);
  assert.match(tools, /case "runtime\.input\.key"/);
  assert.match(tools, /case "runtime\.input\.pointer"/);
  assert.match(tools, /Application\.targetFrameRate =/);
  assert.match(tools, /Time\.timeScale =/);
  assert.match(tools, /LowLevel\.StateEvent/);
  assert.match(tools, /LowLevel\.InputState/);
  assert.match(tools, /GetProperty\("currentTime"/);
  assert.match(tools, /eventTimeProperty\.SetValue\(eventPtr, currentTime/);
  assert.match(tools, /WriteValueIntoEvent/);
  assert.match(tools, /QueueEvent/);
  assert.match(tools, /InputSystem\.Update/);
  assert.match(tools, /phasesProven/);
  assert.match(tools, /observed\.Count == 2/);
  assert.equal(tools.match(/if \(!dryRun\) facade\.Update\(\);/g)?.length, 2);
  assertBefore(tools, "if (!dryRun) facade.Update();", "var before = facade.IsPressed(control);");
  assert.match(tools, /beforePressed/);
  assert.match(tools, /afterPressed/);
  assert.doesNotMatch(tools, /DeltaStateEvent/);
  assert.doesNotMatch(tools, /Type\.GetType\([^\n]*requested|GetMethod\([^\n]*requested/);
});

test("tools discovery is a direct control-plane and keeps the root request identity", async () => {
  const source = await readFile("src/tools/commands.ts", "utf8");
  assert.match(source, /projectCatalogGroups\(flags, rootRuntime, requestId/);
  assert.match(source, /projectCatalogPage\([^\n]+[\s\S]*rootRuntime, requestId/);
  assert.match(source, /projectCatalogDescribe\(name, flags, rootRuntime, requestId/);
  assert.equal(source.match(/dispatchTool\(/g)?.length, 1, "only tools run may enter the Tool dispatcher");
  assert.match(source, /if \(command === "run"\)[\s\S]{0,240}return dispatchTool\(/);
  assert.doesNotMatch(source, /requestId\s*\+|`\$\{requestId\}[_-]/, "discovery must not mint a second request identity");
  for (const obsolete of ["tools/describe", "tools/group-list", "tools/search", "tools/pagination"])
    assert.equal(source.includes(obsolete), false, obsolete);
});

test("runtime JSON rejects duplicate keys, trailing tokens, BOM-body use and non-finite output", async () => {
  const { bridge, json } = await sources();
  assert.match(json, /duplicate key/);
  assert.match(json, /trailing tokens/);
  assert.match(json, /double\.IsNaN/);
  assert.match(json, /double\.IsInfinity/);
  assert.match(bridge, /must not contain a UTF-8 BOM/);
});

test("runtime security, cache, artifacts, and shutdown remain bounded", async () => {
  const { bridge, registry, injection } = await sources();
  const tools = await readFile("unitypkg/Runtime/Tools/UnityCliRuntimeTools.cs", "utf8");
  assert.match(bridge, /MaxQueueItems = 128/);
  assert.match(bridge, /Completion\.Task\.IsCompleted/);
  assert.match(bridge, /private bool FinalizeRuntimeRequest\([\s\S]*?Interlocked\.CompareExchange\(ref request\.ExecutionState, 2, expectedState\)[\s\S]*?request\.Terminal = terminal[\s\S]*?TrySettleRuntimeRequest\(request\)/);
  assert.match(bridge, /TrySettleRuntimeRequest[\s\S]*?pool\.CompleteTrusted\(identity\)[\s\S]*?ExactCurrentCompleted[\s\S]*?DefinitelyAbsent[\s\S]*?request\.Completion\.TrySetResult\(request\.Terminal\)/);
  assert.match(bridge, /RetryPendingRuntimeSettlements[\s\S]*?MaxSettlementRetriesPerTick[\s\S]*?SettlementRetryDelayMs/);
  assert.match(bridge, /private bool TryTakeNextRuntimeRequest\([\s\S]*?lock \(requestCacheSync\)[\s\S]*?stopping\.IsCancellationRequested[\s\S]*?pending\.TryDequeue\(out request\)[\s\S]*?Interlocked\.CompareExchange\(ref request\.ExecutionState, 1, 0\)/);
  assert.match(bridge, /internal RuntimePublicationResult PublishRuntimeRequest\([\s\S]*?lock \(requestCacheSync\)[\s\S]*?requestCache\.TryGet\(requestId, out existing\)[\s\S]*?absoluteDeadlineMs <= UtcNowMs\(\)[\s\S]*?AuthorizeRuntimeAdmission[\s\S]*?PublishPreparedRequestLocked/);
  assert.match(bridge, /internal void StopRuntimeRequestsLocked\(\)[\s\S]*?lock \(requestCacheSync\)[\s\S]*?stopping\.Cancel\(\)[\s\S]*?FinalizeRuntimeRequest[\s\S]*?pending\.TryDequeue[\s\S]*?requestCache\.Clear\(\)/);
  assert.match(bridge, /private static string AwaitRuntimeResponse\([\s\S]*?RuntimeWaitDependencies/);
  assert.match(bridge, /if \(work\.Owner\.FinalizeRuntimeRequest\(work, timeout, 0\)\) return work\.Completion\.Task\.Result;/);
  assert.doesNotMatch(bridge, /FinalizeRuntimeRequest\(work, timeout, 0\)\) work\.Completion\.Task\.Wait\(\)/);
  assert.match(bridge, /E_RUNTIME_QUEUE_FULL/);
  assert.match(bridge, /E_REQUEST_REPLAY_MISMATCH/);
  assertBefore(bridge, "requestCache.TryGet(requestId, out existing)", "absoluteDeadlineMs <= UtcNowMs()");
  assertBefore(bridge, "absoluteDeadlineMs <= UtcNowMs()", "AuthorizeRuntimeAdmission(pool, admission, request, requestDigest");
  assert.match(bridge, /E_RUNTIME_SESSION_STALE/);
  assert.match(bridge, /requestCache\.Clear\(\)/);
  assert.match(bridge, /UnityCliRuntimeTools\.Shutdown\(\)/);
  assert.match(bridge, /HasOnlyKeys\(value, "version", "canonicalizationVersion"/);
  assert.match(bridge, /http:\/\/127\.0\.0\.1:/);
  assert.match(registry, /path \+ "\.tmp"/);
  assert.match(registry, /finally \{ if \(File\.Exists\(temporary\)\) File\.Delete\(temporary\); \}/);
  assert.match(tools, /"runtime-element\.png"/);
  assert.match(tools, /path \+ "\.tmp"/);
  assert.match(tools, /finally \{ if \(File\.Exists\(temporary\)\) File\.Delete\(temporary\); \}/);
  assert.match(injection, /!IsWindowsStandalone\(report\.summary\.platform\) \|\| !IsDevelopmentBuild\(report\)[\s\S]*DeleteExactSidecar\(sidecar\);[\s\S]*return;/);
});

test("Runtime observation implementation is bounded, session-scoped, and version-gated", async () => {
  const tools = await readFile("unitypkg/Runtime/Tools/UnityCliRuntimeTools.cs", "utf8");
  assert.match(tools, /MaxLogEntries = 1000/);
  assert.match(tools, /MaxConsoleLimit = 200/);
  assert.match(tools, /runtimeSessionId/);
  assert.match(tools, /Application\.logMessageReceivedThreaded/);
  assert.match(tools, /E_RUNTIME_LOG_UNPROVEN/);
  assert.match(tools, /#if !UNITY_6000_7_OR_NEWER/);
  assert.match(tools, /E_RUNTIME_ELEMENT_CAPTURE_UNAVAILABLE/);
  assert.match(tools, /runtime-element\.png/);
  assert.match(tools, /MaxPngBytes = 32 \* 1024 \* 1024/);
  assert.doesNotMatch(tools, /base64|Application\.Quit|eval|HotReload/);
});
