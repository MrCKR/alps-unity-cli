import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Cinemachine extension tools.
 *
 * Critical reference notes:
 * - Critically references unity-mcp's Cameras/ManageCamera + CameraHelpers + CameraCreate +
 *   CameraConfigure + CameraControl for the action decomposition and Unity API paths.
 * - Discarded: unity-mcp's single-tool multi-action (manage_camera with action=...) routing is NOT
 *   adopted; alps-unity-cli keeps its "multi-tool single-action" model, so each Cinemachine
 *   capability (ping/create/target/lens/priority/blend/body/aim/noise/brain/force-camera/release-override/extension) is its own tool.
 * - Discarded: unity-mcp resolves Body/Aim components via a preset map keyed by CinemachineCamera
 *   component stage reflection. We keep the preset→component mapping but expose it through the
 *   create tool's --preset flag, not a hidden action router.
 * - Adapted to alps-unity-cli: optional package detection uses FindType("Unity.Cinemachine.CinemachineCamera")
 *   + FindType("Unity.Cinemachine.CinemachineBrain") reflection (no #if UNITY_CINEMACHINE conditional
 *   compile), matching ProBuilder's runtime detection pattern. When missing, tools return
 *   E_CINEMACHINE_PACKAGE_NOT_FOUND with unavailable + next_actions.
 * - SerializedObject is used for nested struct writes (Lens, PrioritySettings, DefaultBlend) to
 *   stay robust across Cinemachine internal field renames, mirroring unity-mcp's defensive path.
 */

const CM_PACKAGE = "com.unity.cinemachine";

/** Common fields shared by all Cinemachine extension tools. */
const CM_COMMON = {
  group: "cinemachine",
  target: "editor" as const,
  requiresPackages: [CM_PACKAGE],
};

export const CINEMACHINE_TOOLS: ToolMetadata[] = [
  {
    ...CM_COMMON,
    name: "cinemachine.ping",
    summary: "Check Cinemachine package availability and reflected camera/brain API status.",
    description: "Check Cinemachine package availability and reflected camera/brain API status without changing the scene. Returns whether CinemachineCamera/CinemachineVirtualCamera, CinemachineBrain, CinemachineExtension, and the Brain override methods are available in the current Unity session.",
    inputSchema: objSchema({}),
    outputSchema: objSchema({
      cinemachine: {
        type: "object",
        properties: {
          installed: { type: "boolean" },
          cmCameraTypeAvailable: { type: "boolean" },
          cmBrainTypeAvailable: { type: "boolean" },
          cmExtensionTypeAvailable: { type: "boolean" },
          forceOverrideAvailable: { type: "boolean" }
        }
      }
    }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Check Cinemachine availability", argv: ["tools", "run", "cinemachine.ping"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.create",
    summary: "Create a Cinemachine virtual camera with an optional preset that configures Body and Aim pipeline components.",
    description: "Create a Cinemachine virtual camera with an optional preset (follow, third_person, freelook, dolly, static, top_down, side_scroller) that configures Body/Aim pipeline components. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({
      preset: { type: "string", enum: ["follow", "third_person", "freelook", "dolly", "static", "top_down", "side_scroller"] },
      name: { type: "string" },
      parent: { type: "string" },
      follow: { type: "string" },
      lookAt: { type: "string" },
      x: { type: "number" },
      y: { type: "number" },
      z: { type: "number" },
      priority: { type: "integer" }
    }),
    destructiveHint: false,
    examples: [
      { description: "Create a follow Cinemachine camera", argv: ["tools", "run", "cinemachine.create", "follow", "--name", "PlayerCam", "--follow", "Player"] },
      { description: "Create a static dolly camera aimed at the scene origin", argv: ["tools", "run", "cinemachine.create", "dolly", "--name", "DollyCam", "--look-at", "Main Camera"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.target",
    summary: "Set the Follow and/or LookAt targets on a Cinemachine virtual camera.",
    description: "Set the Follow and/or LookAt targets on a Cinemachine virtual camera. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, follow: { type: "string" }, lookAt: { type: "string" } }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Set follow target on a Cinemachine camera", argv: ["tools", "run", "cinemachine.target", "PlayerCam", "--follow", "Player"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.lens",
    summary: "Get or set Cinemachine virtual camera lens properties via SerializedObject-backed fields.",
    description: "Get or set lens properties (fieldOfView, nearClipPlane, farClipPlane, orthographicSize, dutch) on a Cinemachine virtual camera via SerializedObject to survive internal field renames. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({
      target: { type: "string" },
      fieldOfView: { type: "number" },
      nearClipPlane: { type: "number" },
      farClipPlane: { type: "number" },
      orthographicSize: { type: "number" },
      dutch: { type: "number" }
    }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Set a 60 degree field of view", argv: ["tools", "run", "cinemachine.lens", "PlayerCam", "--field-of-view", "60"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.priority",
    summary: "Get or set the priority (and enabled flag) on a Cinemachine virtual camera via SerializedObject PrioritySettings struct.",
    description: "Get or set the priority (and enabled flag) on a Cinemachine virtual camera via SerializedObject PrioritySettings struct. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, priority: { type: "integer" }, enabled: { type: "boolean" } }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Boost a camera priority to 20", argv: ["tools", "run", "cinemachine.priority", "PlayerCam", "--priority", "20"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.blend",
    summary: "Get or set the DefaultBlend (style + duration) on the CinemachineBrain of a target camera owner.",
    description: "Get or set the DefaultBlend (style + duration) on the CinemachineBrain of a target camera owner. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, style: { type: "string", enum: ["cut", "ease_in_out", "ease_in", "ease_out", "hard_in", "hard_out", "linear", "soft_in", "soft_out"] }, duration: { type: "number" } }),
    destructiveHint: false,
    examples: [
      { description: "Configure a 2 second ease in/out blend", argv: ["tools", "run", "cinemachine.blend", "--target", "Main Camera", "--style", "ease_in_out", "--duration", "2"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.body",
    summary: "Swap or configure the Cinemachine Body pipeline component on a virtual camera (e.g.",
    description: "Swap or configure the Cinemachine Body pipeline component on a virtual camera (e.g. CinemachineFollow, CinemachinePositionComposer, CinemachineOrbitalFollow, CinemachineSplineDolly, CinemachineThirdPersonFollow). Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, body: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Swap body to CinemachineFollow", argv: ["tools", "run", "cinemachine.body", "PlayerCam", "--body", "CinemachineFollow"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.aim",
    summary: "Swap or configure the Cinemachine Aim pipeline component on a virtual camera (e.g.",
    description: "Swap or configure the Cinemachine Aim pipeline component on a virtual camera (e.g. CinemachineRotationComposer, CinemachineHardLookAt). Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, aim: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Swap aim to CinemachineRotationComposer", argv: ["tools", "run", "cinemachine.aim", "PlayerCam", "--aim", "CinemachineRotationComposer"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.noise",
    summary: "Add, configure or remove a CinemachineBasicMultiChannelPerlin noise component on a virtual camera.",
    description: "Add, configure or remove a CinemachineBasicMultiChannelPerlin noise component on a virtual camera. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, noiseProfile: { type: "string" }, amplitude: { type: "number" }, frequency: { type: "number" }, remove: { type: "boolean" } }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Add a hand-held noise profile", argv: ["tools", "run", "cinemachine.noise", "PlayerCam", "--noise-profile", "Handheld_tele"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.brain",
    summary: "Ensure a CinemachineBrain is attached to the target camera owner, or report the brain's active virtual camera, blending state and active blend.",
    description: "Ensure a CinemachineBrain is attached to the target camera owner, or report the brain's active virtual camera, blending state and active blend. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({ target: { type: "string" }, action: { type: "string", enum: ["ensure", "status"] } }, ["target"]),
    destructiveHint: false,
    examples: [
      { description: "Report brain blending status", argv: ["tools", "run", "cinemachine.brain", "Main Camera", "--action", "status"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.force-camera",
    summary: "Force a CinemachineBrain override to a target virtual camera and return the overrideId to release later.",
    description: "Force a CinemachineBrain override to a target Cinemachine virtual camera using CinemachineBrain.SetCameraOverride. This changes only the current Editor or PlayMode session; it does not save scenes or assets. Store the returned overrideId and pass it to cinemachine.release-override when the forced camera should be released.",
    inputSchema: objSchema({
      target: { type: "string", description: "Cinemachine virtual camera GameObject name, hierarchy path, or instance id to force as camB." },
      brain: { type: "string", description: "Optional GameObject name, path, or instance id that owns the CinemachineBrain. Defaults to the first scene Brain." },
      source: { type: "string", description: "Optional Cinemachine virtual camera used as camA. If omitted, the current active virtual camera is used when available." },
      overrideId: { type: "integer", description: "Existing override id to update. Omit or pass -1 to allocate a new id." },
      weight: { type: "number", description: "Blend weight for the forced target camera. Defaults to 1." },
      deltaTime: { type: "number", description: "Delta time override passed to Cinemachine. Defaults to -1." }
    }, ["target"]),
    outputSchema: objSchema({
      overrideId: { type: "integer" },
      brain: { type: "object" },
      forcedCamera: { type: "object" },
      sourceCamera: { type: ["object", "null"] }
    }),
    destructiveHint: false,
    sideEffects: ["editor-session-state"],
    playModeSupport: "supported",
    examples: [
      { description: "Force a virtual camera and capture the override id", argv: ["tools", "run", "cinemachine.force-camera", "CloseupCam", "--brain", "Main Camera"] },
      { description: "Force with structured input", argv: ["tools", "run", "cinemachine.force-camera", "--input", "{\"target\":\"CloseupCam\",\"brain\":\"Main Camera\",\"weight\":1}"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.release-override",
    summary: "Release a CinemachineBrain camera override by overrideId.",
    description: "Release a camera override previously returned by cinemachine.force-camera using CinemachineBrain.ReleaseCameraOverride. This changes only the current Editor or PlayMode session; it does not save scenes or assets.",
    inputSchema: objSchema({
      overrideId: { type: "integer", description: "Override id returned by cinemachine.force-camera." },
      brain: { type: "string", description: "Optional GameObject name, path, or instance id that owns the CinemachineBrain. Defaults to the first scene Brain." }
    }, ["overrideId"]),
    outputSchema: objSchema({
      overrideId: { type: "integer" },
      brain: { type: "object" },
      released: { type: "boolean" }
    }),
    destructiveHint: false,
    sideEffects: ["editor-session-state"],
    playModeSupport: "supported",
    examples: [
      { description: "Release a forced camera override", argv: ["tools", "run", "cinemachine.release-override", "--input", "{\"overrideId\":12,\"brain\":\"Main Camera\"}"] }
    ]
  },
  {
    ...CM_COMMON,
    name: "cinemachine.extension",
    summary: "List, add or remove CinemachineExtension-derived components on a virtual camera.",
    description: "List, add or remove CinemachineExtension-derived components on a virtual camera. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["list", "add", "remove"] },
      extensionType: { type: "string" }
    }, ["target"]),
    destructiveHint: true,
    examples: [
      { description: "List extensions on a virtual camera", argv: ["tools", "run", "cinemachine.extension", "PlayerCam", "--action", "list"] }
    ]
  }
];
