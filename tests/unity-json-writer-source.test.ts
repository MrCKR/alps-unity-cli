import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("UnityCliSerializer is the one strict JSON reader and writer", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliSerializer.cs", import.meta.url), "utf8");
  assert.match(source, /public static string Serialize\(object value\)/);
  assert.match(source, /public static object Deserialize\(string json, Type targetType\)/);
  assert.match(source, /if \(index != json\.Length\) throw new FormatException\("JSON payload contains trailing tokens\."\)/);
  assert.match(source, /double\.IsNaN\(value\) \|\| double\.IsInfinity\(value\)/);
  assert.match(source, /float\.IsNaN\(single\) \|\| float\.IsInfinity\(single\)/);
  assert.match(source, /Canonical JSON does not support non-finite numbers/);
  assert.doesNotMatch(source, /UnityCliJsonWriter|JavaScriptSerializer|JsonUtility/);
});

test("Bridge HTTP and protocol envelopes serialize through UnityCliSerializer only", async () => {
  const [http, protocol, bridge] = await Promise.all([
    readFile(new URL("../unitypkg/Editor/Bridge/BridgeHttpServer.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/Protocol/ProtocolEnvelopeFactory.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/UnityCliBridge.cs", import.meta.url), "utf8"),
  ]);
  assert.match(http, /BridgeRequest\.DeserializeStrict\(DecodeRequestBody\(rawBody\)\)/);
  assert.match(http, /UnityCliSerializer\.Serialize\(UnityCli\.Protocol\.ProtocolEnvelopeFactory\.(?:Success|Error)/);
  assert.match(bridge, /UnityCliSerializer\.Serialize\(/);
  assert.match(protocol, /public static SuccessEnvelope Success/);
  assert.match(protocol, /ErrorEnvelope/);
  assert.doesNotMatch(`${http}\n${bridge}`, /UnityCliJsonWriter/);
});

test("Bridge fallback errors do not expose exception text or depend on internal logs", async () => {
  const http = await readFile(new URL("../unitypkg/Editor/Bridge/BridgeHttpServer.cs", import.meta.url), "utf8");
  assert.doesNotMatch(http, /Unhandled bridge error:\s*"?\s*\+\s*exception\.Message/);
  assert.doesNotMatch(http, /Check UnityCliBridge\.log/);
  assert.match(http, /WriteLog\("handle_http_unhandled " \+ exception\.GetType\(\)\.Name \+ ": " \+ exception\.Message\)/);
  assert.match(http, /"Unhandled Bridge error\."/);
});

test("typed Tool and Context schemas use the shared serializer value tree", async () => {
  const [tools, contexts, schema] = await Promise.all([
    readFile(new URL("../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/ProjectContexts/ProjectContextRegistry.cs", import.meta.url), "utf8"),
    readFile(new URL("../unitypkg/Editor/ProjectTools/ProjectToolSchemaValidator.cs", import.meta.url), "utf8"),
  ]);
  assert.match(tools, /UnityCliSerializer\.Parse\(definition\.InputSchema\)/);
  assert.match(tools, /UnityCliSerializer\.Parse\(definition\.OutputSchema\)/);
  assert.match(contexts, /UnityCliSerializer\.Parse\(definition\.InputSchema\)/);
  assert.match(contexts, /UnityCliSerializer\.Parse\(definition\.OutputSchema\)/);
  assert.match(schema, /UnityCliSerializer\.(?:Deserialize|Parse)/);
});
