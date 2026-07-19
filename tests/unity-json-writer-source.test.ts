import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);
const removedWriterUrl = new URL("Infrastructure/UnityCliJsonWriter.cs", editorUrl);
const serializerUrl = new URL("Infrastructure/UnityCliSerializer.cs", editorUrl);
const envelopeFactoryUrl = new URL("Protocol/ProtocolEnvelopeFactory.cs", editorUrl);
const protocolEnvelopeUrl = new URL("Protocol/envelope.cs", editorUrl);
const jsonUrl = new URL("Infrastructure/UnityCliJson.cs", editorUrl);
const bridgeHttpUrl = new URL("Bridge/BridgeHttpServer.cs", editorUrl);
const bridgeUrl = new URL("UnityCliBridge.cs", editorUrl);
const batchUrl = new URL("UnityCliBatch.cs", editorUrl);
const projectToolsUrl = new URL("ProjectTools/ProjectToolRegistry.cs", editorUrl);

test("UnityCliSerializer is the only strict JSON implementation", async () => {
  await assert.rejects(access(removedWriterUrl));
  const serializer = await readFile(serializerUrl, "utf8");
  assert.match(serializer, /public static class UnityCliSerializer/);
  assert.match(serializer, /public static string Serialize\(object value\)/);
  assert.match(serializer, /public static T Deserialize<T>\(string json\)/);
  assert.match(serializer, /public static object Parse\(string json\)/);
  assert.match(serializer, /JSON payload contains trailing tokens/);
  assert.match(serializer, /JSON does not support non-finite numbers/);
  assert.match(serializer, /entries\.Sort\(\(left, right\) => string\.CompareOrdinal/);
  assert.doesNotMatch(serializer, /UnityCliJsonWriter/);
});

test("ProtocolEnvelopeFactory owns success and structured error construction", async () => {
  const [factory, protocol, serializer] = await Promise.all([
    readFile(envelopeFactoryUrl, "utf8"), readFile(protocolEnvelopeUrl, "utf8"), readFile(serializerUrl, "utf8")
  ]);

  assert.match(factory, /internal static class ProtocolEnvelopeFactory/);
  assert.match(factory, /public static SuccessEnvelope Success\(string requestId, object data/);
  assert.match(factory, /public static ErrorEnvelope Error\([\s\S]*object details = null,[\s\S]*IEnumerable<string> nextActions = null/);
  assert.match(factory, /Details = details/);
  assert.match(factory, /NextActions = \(nextActions \?\? Array\.Empty<string>\(\)\)/);
  assert.match(protocol, /class SuccessEnvelope/);
  assert.match(protocol, /class ErrorEnvelope/);
  assert.match(protocol, /class ToolError/);
  assert.match(serializer, /public static class UnityCliSerializer/);
  assert.match(serializer, /public static string Serialize\(object value\)/);
  assert.match(serializer, /public static object Deserialize\(string json, Type targetType\)/);
});

test("UnityCliSerializer is the protocol envelope serialization boundary", async () => {
  const [json, bridgeHttp, bridge, batch, projectTools] = await Promise.all([
    readFile(jsonUrl, "utf8"), readFile(bridgeHttpUrl, "utf8"), readFile(bridgeUrl, "utf8"),
    readFile(batchUrl, "utf8"), readFile(projectToolsUrl, "utf8")
  ]);
  const combined = [json, bridgeHttp, bridge, batch].join("\n");

  assert.match(json, /UnityCliSerializer\.Serialize\(UnityCli\.Protocol\.ProtocolEnvelopeFactory\.Success/);
  assert.match(json, /UnityCliSerializer\.Serialize\(UnityCli\.Protocol\.ProtocolEnvelopeFactory\.Error/);
  assert.match(bridgeHttp, /BridgeHealthJson\(\)[\s\S]*UnityCliSerializer\.Serialize\(UnityCli\.Protocol\.ProtocolEnvelopeFactory\.Success/);
  assert.match(bridgeHttp, /E_BRIDGE_ROUTE_NOT_FOUND[\s\S]*ProtocolEnvelopeFactory\.Error/);
  assert.match(bridge, /ProtocolEnvelopeFactory\.Success\(request\.RequestId, data\)/);
  assert.match(batch, /ProtocolEnvelopeFactory\.Success/);
  assert.match(batch, /ProtocolEnvelopeFactory\.Error/);
  assert.doesNotMatch(combined, /UnityCliEnvelope/);

  assert.match(projectTools, /InputSchema = UnityCliSchemaDeriver\.DeriveSchema\(typeof\(TReq\)\)/);
  assert.match(projectTools, /OutputSchema = UnityCliSchemaDeriver\.DeriveSchema\(typeof\(TRes\)\)/);
  assert.match(projectTools, /var terminalData = CompleteProjectToolSuccess/);
  assert.match(projectTools, /RecordToolCompletionEvidence\(request, definition, result, terminalData\)/);
  assert.doesNotMatch(projectTools, /ProtocolEnvelopeFactory\.(?:Success|Error)/);
});
