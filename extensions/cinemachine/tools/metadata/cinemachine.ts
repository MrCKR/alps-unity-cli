import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * Cinemachine extension tools.
 *
 * Critical reference notes (ALP-505 P8):
 * - Critically references unity-mcp's Cameras/ManageCamera + CameraHelpers + CameraCreate +
 *   CameraConfigure + CameraControl for the action decomposition and Unity API paths.
 * - Discarded: unity-mcp's single-tool multi-action (manage_camera with action=...) routing is NOT
 *   adopted; alps-unity-cli keeps its "multi-tool single-action" model, so each Cinemachine
 *   capability (create/target/lens/priority/blend/body/aim/noise/brain/extension) is its own tool.
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
export const CINEMACHINE_TOOLS: ToolMetadata[] = [
  {
    name: "cinemachine.create",
    title: "Cinemachine Create",
    description: "Create a Cinemachine virtual camera with an optional preset (follow, third_person, freelook, dolly, static, top_down, side_scroller) that configures Body/Aim pipeline components. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
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
    readOnlyHint: false,
    destructiveHint: false,
    longRunning: true,
    handler: "hybrid",
    cliCommand: "cinemachine create",
    cancellationSemantics: "Live bridge execution is cooperative and usually short; --job execution can be cancelled with job cancel --id <jobId> using immediate Unity batchmode process termination semantics.",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Create a follow Cinemachine camera", argv: ["cinemachine", "create", "follow", "--name", "PlayerCam", "--follow", "Player"] },
      { description: "Create a static dolly camera aimed at the scene origin", argv: ["cinemachine", "create", "dolly", "--name", "DollyCam", "--look-at", "Main Camera"] }
    ]
  },
  {
    name: "cinemachine.target",
    title: "Cinemachine Target",
    description: "Set the Follow and/or LookAt targets on a Cinemachine virtual camera. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, follow: { type: "string" }, lookAt: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine target",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Set follow target on a Cinemachine camera", argv: ["cinemachine", "target", "PlayerCam", "--follow", "Player"] }
    ]
  },
  {
    name: "cinemachine.lens",
    title: "Cinemachine Lens",
    description: "Get or set lens properties (fieldOfView, nearClipPlane, farClipPlane, orthographicSize, dutch) on a Cinemachine virtual camera via SerializedObject to survive internal field renames. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({
      target: { type: "string" },
      fieldOfView: { type: "number" },
      nearClipPlane: { type: "number" },
      farClipPlane: { type: "number" },
      orthographicSize: { type: "number" },
      dutch: { type: "number" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine lens",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Set a 60 degree field of view", argv: ["cinemachine", "lens", "PlayerCam", "--field-of-view", "60"] }
    ]
  },
  {
    name: "cinemachine.priority",
    title: "Cinemachine Priority",
    description: "Get or set the priority (and enabled flag) on a Cinemachine virtual camera via SerializedObject PrioritySettings struct. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, priority: { type: "integer" }, enabled: { type: "boolean" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine priority",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Boost a camera priority to 20", argv: ["cinemachine", "priority", "PlayerCam", "--priority", "20"] }
    ]
  },
  {
    name: "cinemachine.blend",
    title: "Cinemachine Blend",
    description: "Get or set the DefaultBlend (style + duration) on the CinemachineBrain of a target camera owner. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, style: { type: "string", enum: ["cut", "ease_in_out", "ease_in", "ease_out", "hard_in", "hard_out", "linear", "soft_in", "soft_out"] }, duration: { type: "number" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine blend",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Configure a 2 second ease in/out blend", argv: ["cinemachine", "blend", "--target", "Main Camera", "--style", "ease_in_out", "--duration", "2"] }
    ]
  },
  {
    name: "cinemachine.body",
    title: "Cinemachine Body",
    description: "Swap or configure the Cinemachine Body pipeline component on a virtual camera (e.g. CinemachineFollow, CinemachinePositionComposer, CinemachineOrbitalFollow, CinemachineSplineDolly, CinemachineThirdPersonFollow). Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, body: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine body",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Swap body to CinemachineFollow", argv: ["cinemachine", "body", "PlayerCam", "--body", "CinemachineFollow"] }
    ]
  },
  {
    name: "cinemachine.aim",
    title: "Cinemachine Aim",
    description: "Swap or configure the Cinemachine Aim pipeline component on a virtual camera (e.g. CinemachineRotationComposer, CinemachineHardLookAt). Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, aim: { type: "string" }, property: { type: "string" }, value: { type: "string" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine aim",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Swap aim to CinemachineRotationComposer", argv: ["cinemachine", "aim", "PlayerCam", "--aim", "CinemachineRotationComposer"] }
    ]
  },
  {
    name: "cinemachine.noise",
    title: "Cinemachine Noise",
    description: "Add, configure or remove a CinemachineBasicMultiChannelPerlin noise component on a virtual camera. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, noiseProfile: { type: "string" }, amplitude: { type: "number" }, frequency: { type: "number" }, remove: { type: "boolean" } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine noise",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Add a hand-held noise profile", argv: ["cinemachine", "noise", "PlayerCam", "--noise-profile", "Handheld_tele"] }
    ]
  },
  {
    name: "cinemachine.brain",
    title: "Cinemachine Brain",
    description: "Ensure a CinemachineBrain is attached to the target camera owner, or report the brain's active virtual camera, blending state and active blend. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({ target: { type: "string" }, action: { type: "string", enum: ["ensure", "status"] } }, ["target"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "cinemachine brain",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "Report brain blending status", argv: ["cinemachine", "brain", "Main Camera", "--action", "status"] }
    ]
  },
  {
    name: "cinemachine.extension",
    title: "Cinemachine Extension",
    description: "List, add or remove CinemachineExtension-derived components on a virtual camera. Requires the Cinemachine package (com.unity.cinemachine) to be installed in the target project.",
    group: "cinemachine",
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["list", "add", "remove"] },
      extensionType: { type: "string" }
    }, ["target"]),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "cinemachine extension",
    docsTopic: "cinemachine",
    requiresPackages: ["com.unity.cinemachine"],
    examples: [
      { description: "List extensions on a virtual camera", argv: ["cinemachine", "extension", "PlayerCam", "--action", "list"] }
    ]
  }
];
