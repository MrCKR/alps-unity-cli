import { buildTopics } from "../../builtin-docs.js";
import { defineTool } from "../define.js";

export interface DocsListRequest {
  format?: string;
}

export interface DocsListTopic {
  key: string;
  title: string;
  summary: string;
  toolCount: number;
}

export interface DocsListResult {
  count: number;
  topics: DocsListTopic[];
  text?: string;
}

export default defineTool<DocsListRequest, DocsListResult>({
  name: "docs.list",
  summary: "List built-in Unity CLI documentation topics without requiring a Unity Editor connection.",
  description: "List built-in Unity CLI documentation topics without requiring a Unity Editor connection. Use this before docs.topic when the topic key is unknown.",
  group: "docs",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "List built-in CLI doc topics", argv: ["tools", "run", "docs.list", "--input", "{}"] },
    { description: "List topics as text", argv: ["tools", "run", "docs.list", "--input", "{\"format\":\"text\"}"] }
  ],
  /** 列出内置文档 topic；projectTools 来自运行时发现结果,用于保留自定义工具 topic。 */
  execute(request, context) {
    const topics = buildTopics(context.projectTools)
      .map((topic) => ({ key: topic.key, title: topic.title, summary: topic.summary, toolCount: topic.tools.length }))
      .sort((a, b) => a.key.localeCompare(b.key));
    const data: DocsListResult = {
      count: topics.length,
      topics
    };
    if (request.format === "text") {
      data.text = topics.map((topic) => `${topic.key} (${topic.toolCount} tools) - ${topic.title}: ${topic.summary}`).join("\n");
    }
    return data;
  }
});
