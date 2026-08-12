import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const registryPath = "unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs";
const officialPath = "unitypkg/Editor/ProjectTools/OfficialAttributeToolRegistry.cs";
const ownershipPath = "unitypkg/Editor/Infrastructure/RegistryOwnershipResolver.cs";
const coordinatorPath = "unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs";
const fixturePath = "Dev/UnityProject/Assets/Editor/UnityCLI/official-attribute-fixtures.cs";
const commandDocsPath = "docs/command-execution.md";

test("official attribute discovery uses exact full names without a package compile dependency", async () => {
  const [registry, official] = await Promise.all([readFile(registryPath, "utf8"), readFile(officialPath, "utf8")]);
  assert.match(registry, /TryRegisterOfficialAttributeTools\(assembly, type\)/);
  assert.match(official, /Unity\.Pipeline\.Commands\.CliCommandAttribute/);
  assert.match(official, /Unity\.Pipeline\.Commands\.CliArgAttribute/);
  assert.match(official, /Unity\.Pipeline\.Commands\.IStructuredCommandInput/);
  assert.match(official, /CustomAttributeData\.GetCustomAttributes/);
  assert.doesNotMatch(official, /using Unity\.Pipeline/);
});

test("Pipeline package provider在注册前抑制且其他attribute继续共享pending发布", async () => {
  const [registry, official, ownership] = await Promise.all([
    readFile(registryPath, "utf8"),
    readFile(officialPath, "utf8"),
    readFile(ownershipPath, "utf8"),
  ]);
  assert.match(registry, /var pending = new Dictionary<string, RegisteredTool>/);
  assert.match(registry, /ProjectTools\.Clear\(\)/);
  assert.match(registry, /ReflectionTypeLoadException exception\)[^\n]*throw new InvalidOperationException/);
  assert.doesNotMatch(registry, /ReflectionTypeLoadException exception\)[^\n]*types = exception\.Types/);
  assert.match(registry, /catch \(Exception exception\)[\s\S]*?E_TOOL_REGISTRY_UNHEALTHY/);
  assert.match(registry, /discoveryTools = null/);
  assert.match(official, /SuppressedOfficialAttributePackage = "com\.unity\.pipeline"/);
  assert.match(official, /PackageInfo\.FindForAssembly\(assembly\)/);
  assert.match(official, /package\.name, SuppressedOfficialAttributePackage, StringComparison\.Ordinal/);
  assert.match(official, /if \(IsSuppressedOfficialAttributeProvider\(assembly\)\)[\s\S]*?return;[\s\S]*?foreach \(var method in methods\)/);
  assert.match(official, /Official CLI command method must be static/);
  assert.match(official, /duplicate projected parameter name/);
  assert.doesNotMatch(ownership, /PackageInfo\.FindForAssembly|IsOwnedByPackage/);
  assert.match(official, /var toolName = OfficialAttributeToolPrefix \+ name/);
  assert.match(official, /Name = toolName/);
  assert.match(official, /Group = OfficialAttributeToolGroup/);
  assert.match(official, /RegisterProjectTool\(definition\)/);
  assert.match(registry, /Duplicate tool registration/);
  assert.match(registry, /ValidateOfficialAttributeNamespace\(definition\)/);
  assert.match(registry, /Native Tool cannot use the reserved UnityPipeline_/);
});

test("official descriptors expose the conservative machine contract", async () => {
  const official = await readFile(officialPath, "utf8");
  for (const pattern of [
    /DestructiveHint = true/,
    /SideEffects = new\[\] \{ "arbitrary-method-invocation" \}/,
    /Permissions = new\[\] \{ "execute-user-code" \}/,
    /SupportsDryRun = false/,
    /ContractProfile = "official-attribute"/,
    /PlayModeSupport = playModeSupport/,
    /"official-method-return-v1"/,
    /"method-may-still-be-running"/,
  ]) assert.match(official, pattern);
});

test("official attribute examples distinguish defaults from caller intent", async () => {
  const official = await readFile(officialPath, "utf8");
  assert.match(official, /Schema defaults and examples describe valid default behavior, not caller intent/);
  assert.match(official, /use caller-requested business values on the first execution/);
  assert.match(official, /Use these values only for default behavior/);
  assert.match(official, /replace them with caller-requested business values before execution/);
  assert.match(official, /schema\["default"\] = NormalizeOfficialResult/);
});

test("official attribute公开profile与机器契约保持一致", async () => {
  const docs = await readFile(commandDocsPath, "utf8");
  for (const pattern of [
    /\[CliCommand\]/,
    /\[CliArg\]/,
    /IStructuredCommandInput/,
    /Task<T>/,
    /async void/,
    /RuntimeOnly=true/,
    /x-unityCliAnyJson/,
    /official-attribute/,
    /arbitrary-method-invocation/,
    /execute-user-code/,
    /supportsDryRun:false/,
    /只跨一次effect barrier/,
    /超时不声称取消底层method/,
    /任务明确指定业务值时，第一次执行必须使用任务值/,
    /不能先以default\/example做连通性或probe调用/,
  ]) assert.match(docs, pattern);
});

test("official invocation crosses one barrier and does not claim Task cancellation", async () => {
  const [official, registry, coordinator] = await Promise.all([
    readFile(officialPath, "utf8"),
    readFile(registryPath, "utf8"),
    readFile(coordinatorPath, "utf8"),
  ]);
  assert.equal((official.match(/context\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(official, /Task\.WhenAny\(task, cancellation\)/);
  assert.match(official, /throw new OperationCanceledException\(cancellationToken\)/);
  assert.match(official, /task\.ContinueWith/);
  assert.match(official, /FaultObservationToken/);
  assert.doesNotMatch(official, /CancellationToken\.None/);
  assert.match(official, /binding\.DeclaredResultType == typeof\(void\)/);
  assert.match(official, /attributed method did not return a trustworthy terminal result/);
  assert.match(registry, /innerBce[\s\S]*?OfficialAttributeBinding != null[\s\S]*?PostEffectExecutionTimeout/);
  assert.match(registry, /try[\s\S]*?UnityCliSerializer\.Serialize\(result\)[\s\S]*?catch \(BridgeCommandException exception\)/);
  assert.match(registry, /catch \(Exception exception\)[\s\S]*?"postcondition"/);
  assert.match(coordinator, /official-method-return-v1/);
});

test("official conversion and structured schema fail closed", async () => {
  const official = await readFile(officialPath, "utf8");
  assert.match(official, /duplicate projected parameter name/);
  assert.match(official, /cannot contain a recursive type cycle/);
  assert.match(official, /Official CLI command parameter type is unsupported/);
  assert.match(official, /Attributed command input could not be converted at \$\./);
  assert.match(official, /"E_SCHEMA_INVALID"/);
  assert.match(official, /TryOfficialDictionaryValueType/);
  assert.match(official, /catch \(InvalidOperationException\) \{ return false; \}/);
  assert.match(official, /OfficialTypeAcceptsNull/);
  assert.match(official, /parameter\.Required && OfficialRequiredValueMissing/);
  assert.match(official, /IsNewtonsoftJsonTokenType/);
  assert.match(official, /GetMethod\("DeserializeObject"/);
  assert.match(official, /TryNormalizeWithNewtonsoftJson/);
  assert.match(official, /FindNewtonsoftJsonConvert/);
  assert.doesNotMatch(official, /using Newtonsoft\.Json/);
  assert.match(official, /IsAwaitableType\(returnType\)/);
  assert.match(official, /asynchronous return type must be Task or Task<T>/);
  assert.match(official, /ValidateOfficialAttributeValue/);
  assert.match(official, /knownMembers\.ContainsKey/);
  assert.match(official, /member\.Required[\s\S]*?required property/);
  assert.match(official, /string\.Equals\(type\.FullName, OfficialStructuredInput/);
  assert.match(official, /NormalizeOfficialInput\(child, member\.Type\)/);
  assert.match(official, /unknown property/);
  assert.doesNotMatch(official, /conversion failed.*default/i);
});

test("Dev fixture mirrors official authoring shape and covers sync Task structured runtime timeout", async () => {
  const fixture = await readFile(fixturePath, "utf8");
  assert.match(fixture, /namespace Unity\.Pipeline\.Commands/);
  for (const name of ["sync", "task", "structured", "any-output", "dictionary", "jobject", "result-getter-throw", "runtime", "timeout", "timeout-status", "throw"])
    assert.match(fixture, new RegExp(`validation\\.official\\.${name.replace("-", "\\-")}`));
  assert.match(fixture, /\[CliCommand\("test"/);
  assert.match(fixture, /Name = "test"/);
  assert.match(fixture, /IUnityCliTool<UnityCliNativeTestTool\.Request/);
  assert.match(fixture, /IStructuredCommandInput/);
  assert.match(fixture, /Task<FixtureResult>/);
  assert.match(fixture, /RuntimeOnly = true/);
  assert.match(fixture, /BridgeCommandException/);
  assert.match(fixture, /fixture-sensitive-value/);
  assert.match(fixture, /Dictionary<string, StructuredInput>/);
  assert.match(fixture, /getter-sensitive-value/);
});
