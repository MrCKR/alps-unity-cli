import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const readSource = (relativePath: string) => readFileSync(join(root, relativePath), "utf8");

const bridge = readSource("unitypkg/Editor/UnityCliBridge.cs");
const registry = readSource("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs");
const runtimeExecutor = readSource("unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs");
const json = readSource("unitypkg/Editor/Infrastructure/UnityCliJson.cs");
const exception = readSource("unitypkg/Editor/Bridge/BridgeCommandException.cs");
const errors = readSource("extensions/probuilder/unitypkg/Editor/ProBuilder/ProBuilderErrors.cs");
const reflection = readSource("extensions/probuilder/unitypkg/Editor/ProBuilder/ProBuilderReflection.cs");
const toolDirectory = join(root, "extensions", "probuilder", "unitypkg", "Editor", "ProBuilder");
const toolSources = readdirSync(toolDirectory)
  .filter((file) => file.endsWith(".cs") && !file.startsWith("ProBuilder"))
  .map((file) => ({ file, source: readFileSync(join(toolDirectory, file), "utf8") }));

test("BridgeCommandException carries structured Details and NextActions", () => {
  assert.match(exception, /public sealed class BridgeCommandException/);
  assert.match(exception, /public object Details \{ get; private set; \}/);
  assert.match(exception, /public IReadOnlyList<string> NextActions \{ get; private set; \}/);
  assert.match(exception, /object details, IEnumerable<string> nextActions/);
  assert.match(exception, /Details = details/);
  assert.match(exception, /NextActions = \(nextActions \?\? Array\.Empty<string>\(\)\)/);
});

test("Bridge exception serialization forwards structured fields to ProtocolEnvelopeFactory", () => {
  assert.match(json, /SerializeBridgeExceptionEnvelope\(string requestId, BridgeCommandException exception\)/);
  assert.match(json, /ProtocolEnvelopeFactory\.Error\([\s\S]*exception\.Details,[\s\S]*exception\.NextActions,[\s\S]*exception\.Transient/);
  assert.doesNotMatch(json, /BridgeExceptionErrorJson/);
});

test("project and runtime typed routes rethrow structured errors", () => {
  for (const [name, source] of [["project", registry], ["runtime", runtimeExecutor]] as const) {
    assert.match(source, /catch \(BridgeCommandException bce\)[\s\S]*throw;/, `${name} route must preserve BridgeCommandException`);
    assert.match(source, /catch \(TargetInvocationException exception\)[\s\S]*inner as BridgeCommandException[\s\S]*throw innerBce/, `${name} route must unwrap reflected BridgeCommandException`);
    assert.match(source, /catch \(Exception exception\)[\s\S]*throw new BridgeCommandException\("E_TOOL_EXECUTION_FAILED"/, `${name} route must structure unexpected failures`);
    assert.doesNotMatch(source, /return BridgeExceptionErrorJson/);
  }
});

test("Execute is the single success and error envelope boundary", () => {
  const start = bridge.indexOf("private static async ValueTask<string> Execute(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)");
  const end = bridge.indexOf("private static async ValueTask<object> ExecuteData", start);
  const execute = bridge.slice(start, end);

  assert.ok(start >= 0 && end > start);
  assert.match(execute, /await ExecuteData\(request, cancellationToken\)/);
  assert.match(execute, /ProtocolEnvelopeFactory\.Success\(request\.RequestId, data\)/);
  assert.match(execute, /SerializeBridgeExceptionEnvelope\(request\.RequestId, exception\)/);
  assert.doesNotMatch(runtimeExecutor, /ProtocolEnvelopeFactory\.(?:Success|Error)/);
  assert.match(registry, /SerializeTerminalSuccess[\s\S]*ProtocolEnvelopeFactory\.Success/);
});

test("ProBuilder error factories create BridgeCommandException with machine-readable details and actions", () => {
  for (const method of ["PackageNotAvailable", "TargetNotFound", "SchemaInvalid", "OperationFailed", "OutcomeUnknown"]) {
    assert.match(errors, new RegExp(`public static UnityCliBridge\\.BridgeCommandException ${method}\\(`));
  }
  assert.match(errors, /new Dictionary<string, object>/);
  assert.match(errors, /new\[\] \{ "install_com_unity_probuilder/);
  assert.match(errors, /new\[\] \{ "verify_target_gameobject_exists/);
  assert.match(errors, /new\[\] \{ "check_unity_console_for_errors"/);
  assert.doesNotMatch(errors, /\.Prop\("ok"|Success\(/);
});

test("ProBuilder reflection retries optional type resolution after reload", () => {
  assert.match(reflection, /private sealed class RetryingType/);
  assert.match(reflection, /if \(value == null\) value = FindType\(fullName\)/);
  assert.doesNotMatch(reflection, /Lazy<Type>/);
});

test("ProBuilder reflection matches the installed 5.x shape and collection APIs", () => {
  assert.match(reflection, /GetMethod\("CreateShape", new\[\] \{ ShapeTypeType\.Value, PivotLocationType\.Value \}\)/);
  assert.match(reflection, /created as GameObject \?\? \(created as Component\)\?\.gameObject/);
  assert.doesNotMatch(reflection, /GetMethod\("CreateShape", new\[\] \{ ShapeTypeType\.Value \}\)/);
  assert.equal(reflection.includes(["legacy", "Method"].join("")), false);
  assert.match(reflection, /GetProperty\("faceCount"\)/);
  assert.match(reflection, /GetProperty\("vertexCount"\)/);
  assert.match(reflection, /facesProp\.GetValue\(mesh\) as System\.Collections\.IList/);
  assert.match(reflection, /GetMethod\("ToMesh", new\[\] \{ typeof\(MeshTopology\) \}\)/);
});

test("ProBuilder tools throw errors and return bare typed results", () => {
  const errorMethods = ["PackageNotAvailable", "TargetNotFound", "SchemaInvalid", "OperationFailed", "OutcomeUnknown"];
  for (const { file, source } of toolSources) {
    for (const method of errorMethods) {
      assert.ok(!source.includes(`return ProBuilderErrors.${method}(`), `${file}: structured errors must be thrown`);
    }
    assert.doesNotMatch(source, /ProBuilderErrors\.Success|ProtocolEnvelopeFactory\.(?:Success|Error)/, `${file}: tools must not create envelopes`);
    if (file !== "ping.cs") assert.match(source, /throw ProBuilderErrors\./, `${file}: error paths must throw BridgeCommandException`);
  }
});
