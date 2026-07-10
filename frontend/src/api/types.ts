export type Role = "user" | "assistant" | "tool" | "system";

export interface Artifact {
  id: string;
  kind: "docx" | "pptx";
  fileName: string;
  blobPath: string;
  sizeBytes: number;
  createdAt: string;
}

export interface Message {
  id: string;
  threadId: string;
  runId?: string;
  role: Role;
  content: string;
  toolName?: string;
  sequence: number;
  artifacts: Artifact[];
  streaming?: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface ThreadListItem {
  id: string;
  title: string;
  updatedAt: string;
}

export interface ChatThread {
  id: string;
  userId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface SendMessageResponse {
  runId: string;
  threadId: string;
  userMessageId: string;
}

/** Server-sent event payload emitted by the backend during a run. */
export interface AgentEvent {
  type:
    | "run"
    | "message"
    | "delta"
    | "tool"
    | "artifact"
    | "message_done"
    | "error"
    | "done";
  seq?: number;
  messageId?: string;
  role?: Role;
  delta?: string;
  content?: string;
  tool?: string;
  toolStatus?: "started" | "completed" | "failed";
  summary?: string;
  artifact?: Artifact;
  runId?: string;
  status?: string;
  error?: string;
}
