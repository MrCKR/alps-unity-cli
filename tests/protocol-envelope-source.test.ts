import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("C# envelope factory matches the generated public protocol DTO", async () => {
  const [factory, generated] = await Promise.all([
    readFile("unitypkg/Editor/Protocol/ProtocolEnvelopeFactory.cs", "utf8"),
    readFile("unitypkg/Editor/Protocol/envelope.cs", "utf8")
  ]);

  assert.match(generated, /class SuccessEnvelope[\s\S]*public bool Ok[\s\S]*public string RequestId[\s\S]*public object Data[\s\S]*public System\.Collections\.Generic\.List<string> Warnings/);
  assert.match(generated, /class ErrorEnvelope[\s\S]*public bool Ok[\s\S]*public string RequestId[\s\S]*public ToolError Error/);
  assert.doesNotMatch(generated, /JobId|LogsRef|DurationMs/);
  assert.doesNotMatch(factory, /\.DurationMs\s*=|durationMs\s*=/);
  assert.match(factory, /Success\(string requestId, object data\)/);
  assert.match(factory, /Details = SanitizeErrorDetails\(details\)/);
  assert.match(factory, /UnityCliSerializer\.Parse\(UnityCliSerializer\.Serialize\(details\)\)/);
  assert.match(factory, /ForbiddenErrorDetailKeys/);
  for (const key of ["endpoint", "listenerid", "hwnd", "commandline", "processid", "logpath"]) assert.match(factory, new RegExp(`"${key}"`));
});
