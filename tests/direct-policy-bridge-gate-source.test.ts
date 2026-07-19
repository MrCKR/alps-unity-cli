import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

const root = process.cwd();

test("Bridge rejects prohibited direct tools before journal acceptance or handler execution", async () => {
  const [source, supervisor, http, bridgeTypes, compile, state, bridge] = await Promise.all([
    readFile(join(root, "unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs"), "utf8"),
    readFile(join(root, "src/bridge/session-supervisor.ts"), "utf8"),
    readFile(join(root, "unitypkg/Editor/Bridge/BridgeHttpServer.cs"), "utf8"),
    readFile(join(root, "src/bridge/types.ts"), "utf8"),
    readFile(join(root, "src/tools/editor/compile.ts"), "utf8"),
    readFile(join(root, "src/contexts/editor/state.ts"), "utf8"),
    readFile(join(root, "unitypkg/Editor/UnityCliBridge.cs"), "utf8"),
  ]);
  const policy = source.indexOf("var directAllowed = string.Equals(contract.DirectInvocationPolicy");
  const error = source.indexOf("E_DIRECT_BUSINESS_PROOF_UNAVAILABLE", policy);
  const journal = source.indexOf("AcceptOrRecover(request", policy);

  assert.ok(policy >= 0, "completion direct policy must be enforced in the Bridge coordinator");
  assert.ok(error > policy, "prohibited direct delivery must fail in the HTTP coordinator");
  assert.ok(journal > error, "direct policy rejection must happen before request journal acceptance");
  assert.doesNotMatch(source.slice(policy, journal), /FrozenDeliveryPolicy|Application\.isBatchMode|preservedFrozenInvocation/);
  assert.doesNotMatch(supervisor, /x-unity-cli-invocation-origin|invocationOrigin/);
  assert.doesNotMatch(http, /x-unity-cli-invocation-origin|internalOrigin|invocationOrigin/);
  assert.doesNotMatch(bridgeTypes, /internalCommandOriginKey|withInternalCommandOrigin/);
  assert.match(compile, /withTransitionBinding/);
  assert.match(state, /withObservationSelection/);
  assert.match(bridge, /ExecuteBatchCommandAsync[\s\S]*response = await Execute\(request, timeout\.Token\)/);
  assert.doesNotMatch(source.slice(policy, journal), /request\.Input|request\.Execution.*(?:origin|channel)/i);
});
