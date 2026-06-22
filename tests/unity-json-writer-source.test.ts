import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";

const writerUrl = new URL("../../unitypkg/Editor/Infrastructure/UnityCliJsonWriter.cs", import.meta.url);
const envelopeUrl = new URL("../../unitypkg/Editor/Infrastructure/UnityCliEnvelope.cs", import.meta.url);
const jsonUrl = new URL("../../unitypkg/Editor/Infrastructure/UnityCliJson.cs", import.meta.url);
const bridgeHttpUrl = new URL("../../unitypkg/Editor/Bridge/BridgeHttpServer.cs", import.meta.url);
const codeExecUrl = new URL("../../unitypkg/Editor/Commands/CodeExecCommand.cs", import.meta.url);
const batchUrl = new URL("../../unitypkg/Editor/UnityCliBatch.cs", import.meta.url);
const projectToolsUrl = new URL("../../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url);

test("Unity JSON writer supports core JSON values and validates raw JSON", async () => {
  const writer = await readFile(writerUrl, "utf8");

  assert.match(writer, /internal sealed class UnityCliJsonWriter/);
  assert.match(writer, /BeginObject\(\)/);
  assert.match(writer, /BeginArray\(\)/);
  assert.match(writer, /Prop\(string name, string value\)/);
  assert.match(writer, /Prop\(string name, bool value\)/);
  assert.match(writer, /Prop\(string name, long value\)/);
  assert.match(writer, /PropNull\(string name\)/);
  assert.match(writer, /PropRaw\(string name, string rawJson\)/);
  assert.match(writer, /RawValue\(string rawJson\)/);
  assert.match(writer, /CultureInfo\.InvariantCulture/);
  assert.match(writer, /IsCompleteJsonValue\(string json\)/);
  assert.match(writer, /ParseObject\(string json, ref int index\)/);
  assert.match(writer, /ParseArray\(string json, ref int index\)/);
  assert.match(writer, /ParseString\(string json, ref int index\)/);
  assert.match(writer, /ParseNumber\(string json, ref int index\)/);
  assert.match(writer, /WriteRawOrString\(rawJson\)/);
  assert.match(writer, /WriteString\(rawJson\)/);
});

test("Unity envelope centralizes success, error, details and nextActions", async () => {
  const envelope = await readFile(envelopeUrl, "utf8");
  const json = await readFile(jsonUrl, "utf8");

  assert.match(envelope, /internal static class UnityCliEnvelope/);
  assert.match(envelope, /Success\(string requestId, string dataJson\)/);
  assert.match(envelope, /Error\(string requestId, string code, string message, string detailsJson, IEnumerable<string> nextActions\)/);
  assert.match(envelope, /PropRaw\("details", ObjectOrEmpty\(detailsJson\)\)/);
  assert.match(envelope, /PropArray\("nextActions"/);
  assert.match(envelope, /ObjectOrEmpty\(string rawJson\)/);
  assert.match(envelope, /ArrayOrEmpty\(string rawJson\)/);
  assert.match(json, /return UnityCliEnvelope\.Success\(dataJson\)/);
  assert.match(json, /return UnityCliEnvelope\.Error\(code, message, nextAction\)/);
  assert.match(json, /return UnityCliEnvelope\.ErrorWithDetails\(code, message, detailsJson, nextAction\)/);
  assert.match(json, /return UnityCliJsonWriter\.RawOrString\(value\)/);
});

test("high-impact Unity outputs use writer or envelope infrastructure", async () => {
  const bridgeHttp = await readFile(bridgeHttpUrl, "utf8");
  const codeExec = await readFile(codeExecUrl, "utf8");
  const batch = await readFile(batchUrl, "utf8");
  const projectTools = await readFile(projectToolsUrl, "utf8");

  assert.match(bridgeHttp, /BridgeHealthJson\(\)[\s\S]*UnityCliEnvelope\.Success\(data\)/);
  assert.match(bridgeHttp, /UnityCliEnvelope\.Error\("E_BRIDGE_ROUTE_NOT_FOUND"/);
  assert.match(codeExec, /CodeSuccessDataJson[\s\S]*new UnityCliJsonWriter\(\)\.BeginObject\(\)/);
  assert.match(codeExec, /CodeDetailsJson[\s\S]*PropRaw\("diagnostics"/);
  assert.match(batch, /SuccessEnvelope[\s\S]*UnityCliEnvelope\.Success\(requestId, dataJson\)/);
  assert.match(batch, /ErrorEnvelope[\s\S]*UnityCliEnvelope\.Error\(requestId, code, message, detailsJson, actions\)/);
  assert.match(projectTools, /ProjectToolDefinitionJson[\s\S]*PropRaw\("inputSchema", definition\.InputSchema\)/);
  assert.match(projectTools, /ProjectToolDefinitionJson[\s\S]*PropRaw\("outputSchema", definition\.OutputSchema\)/);
  assert.match(projectTools, /ProjectToolRun[\s\S]*PropRaw\("result", resultJson\)/);
  assert.match(projectTools, /ProjectToolFingerprintJson[\s\S]*new UnityCliJsonWriter\(\)\.BeginObject\(\)/);
  assert.doesNotMatch(projectTools, /JsonLiteralOrString\(resultJson\)/);
});
