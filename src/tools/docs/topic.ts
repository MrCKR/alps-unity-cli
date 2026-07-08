import { buildTopics } from "../../builtin-docs.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface DocsTopicRequest {
  topic: string;
  format?: string;
}

export interface DocsTopicResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  topic: string;
  title: string;
  summary: string;
  tools: string[];
  guide?: string;
  text?: string;
}

export default defineTool<DocsTopicRequest, DocsTopicResult>({
  name: "docs.topic",
  summary: "Read one built-in Unity CLI documentation topic by key.",
  description: "Read one built-in Unity CLI documentation topic by key. This is offline CLI guidance, not Unity ScriptReference content.",
  group: "docs",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Read the scene topic", argv: ["tools", "run", "docs.topic", "--input", "{\"topic\":\"scene\"}"] }
  ],
  /** 读取单个内置 topic；找不到时返回稳定 docs 错误码而不是泛化失败。 */
  execute(request, context) {
    if (!request.topic) {
      throw new StructuredToolError("DOCS_TOPIC_REQUIRED", "Pass a topic: unity_cli tools run docs.topic <topic>.", ["Run unity_cli tools run docs.list to list built-in topics."]);
    }
    const topics = buildTopics(context.projectTools);
    const doc = topics.find((candidate) => candidate.key === request.topic);
    if (!doc) {
      const available = topics.map((candidate) => candidate.key).sort();
      throw new StructuredToolError("DOCS_TOPIC_NOT_FOUND", `Documentation topic not found: ${request.topic}.`, [
        `Available topics: ${available.join(", ")}.`,
        "Run unity_cli tools group to see tool groups, then tools group list <group> to see tools in a group."
      ]);
    }

    const data: DocsTopicResult = {
      command: "docs.topic",
      mode: "builtin",
      requiresUnity: false,
      topic: doc.key,
      title: doc.title,
      summary: doc.summary,
      tools: doc.tools,
      ...(doc.guide ? { guide: doc.guide } : {})
    };
    if (request.format === "text") {
      const lines = [
        `# ${doc.title}`,
        "",
        doc.summary,
        "",
        "Tools:",
        ...doc.tools.map((tool) => `  - ${tool}`)
      ];
      if (doc.guide) {
        lines.push("", "Guide:", `  ${doc.guide}`);
      }
      data.text = lines.join("\n");
    }
    return data;
  }
});
