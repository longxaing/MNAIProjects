import { authEnabled, getToken } from "../auth/auth";
import type {
  AgentEvent,
  ChatThread,
  Message,
  SendMessageResponse,
  ThreadListItem
} from "./types";

const BASE = import.meta.env.VITE_API_BASE?.trim() || "";

async function buildHeaders(hasBody: boolean): Promise<Record<string, string>> {
  const headers: Record<string, string> = {};
  if (hasBody) headers["Content-Type"] = "application/json";

  const token = await getToken();
  if (token) {
    headers["Authorization"] = `Bearer ${token}`;
  }
  if (!authEnabled) {
    // Local dev: identify the user to the backend DevAuth handler.
    headers["X-Debug-User"] = localStorage.getItem("debugUser") || "local-dev-user";
  }
  return headers;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const hasBody = init?.body != null;
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { ...(await buildHeaders(hasBody)), ...(init?.headers ?? {}) }
  });
  if (!res.ok) {
    throw new Error(`${res.status} ${res.statusText}: ${await res.text()}`);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  listThreads: () => request<ThreadListItem[]>("/api/threads"),

  createThread: (title?: string) =>
    request<ChatThread>("/api/threads", {
      method: "POST",
      body: JSON.stringify({ title })
    }),

  deleteThread: (threadId: string) =>
    request<void>(`/api/threads/${threadId}`, { method: "DELETE" }),

  getMessages: (threadId: string) =>
    request<Message[]>(`/api/threads/${threadId}/messages`),

  sendMessage: (threadId: string, content: string) =>
    request<SendMessageResponse>(`/api/threads/${threadId}/messages`, {
      method: "POST",
      body: JSON.stringify({ content })
    })
};

/**
 * Consume a run's SSE stream via fetch + ReadableStream (so we can attach the
 * Authorization header, which EventSource cannot do).
 */
export async function streamRun(
  threadId: string,
  runId: string,
  onEvent: (evt: AgentEvent) => void,
  signal: AbortSignal
): Promise<void> {
  const res = await fetch(`${BASE}/api/threads/${threadId}/runs/${runId}/stream`, {
    headers: await buildHeaders(false),
    signal
  });
  if (!res.ok || !res.body) {
    throw new Error(`Stream failed: ${res.status} ${res.statusText}`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    const frames = buffer.split("\n\n");
    buffer = frames.pop() ?? "";
    for (const frame of frames) {
      const dataLine = frame.split("\n").find((l) => l.startsWith("data:"));
      if (!dataLine) continue;
      const json = dataLine.slice(5).trim();
      if (!json) continue;
      try {
        onEvent(JSON.parse(json) as AgentEvent);
      } catch {
        // Ignore malformed frames.
      }
    }
  }
}

export async function downloadArtifact(url: string, fileName: string): Promise<void> {
  // Absolute SAS links can be opened directly; relative proxy routes need auth.
  if (/^https?:\/\//i.test(url)) {
    window.open(url, "_blank", "noopener");
    return;
  }
  const res = await fetch(`${BASE}${url}`, { headers: await buildHeaders(false) });
  if (!res.ok) throw new Error(`Download failed: ${res.status}`);
  const blob = await res.blob();
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = objectUrl;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(objectUrl);
}
