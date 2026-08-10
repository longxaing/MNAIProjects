import { authEnabled, getToken } from "../auth/auth";
import type {
  AgentEvent,
  Attachment,
  ChatThread,
  Message,
  SendMessageResponse,
  ThreadListItem
} from "./types";

const BASE = import.meta.env.VITE_API_BASE?.trim() || "";

async function buildHeaders(jsonBody: boolean): Promise<Record<string, string>> {
  const headers: Record<string, string> = {};
  if (jsonBody) headers["Content-Type"] = "application/json";

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
  // Only set a JSON content type for plain-body requests. FormData must keep its
  // browser-generated multipart boundary, so we detect and skip it.
  const jsonBody = init?.body != null && !(init.body instanceof FormData);
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { ...(await buildHeaders(jsonBody)), ...(init?.headers ?? {}) }
  });
  if (!res.ok) {
    throw new Error(`${res.status} ${res.statusText}: ${await res.text()}`);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const api = {
  /**
   * Get-or-create the current user. Called right after sign-in so the backend
   * records the user (first login) and refreshes last-seen on return visits.
   */
  getMe: () => request<unknown>("/api/users/me"),

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

  sendMessage: (threadId: string, content: string, attachments?: Attachment[]) =>
    request<SendMessageResponse>(`/api/threads/${threadId}/messages`, {
      method: "POST",
      body: JSON.stringify({ content, attachments: attachments ?? [] })
    }),

  uploadFile: async (threadId: string, file: File): Promise<Attachment> => {
    const form = new FormData();
    form.append("file", file, file.name);
    // Note: do NOT set Content-Type; the browser sets the multipart boundary.
    return request<Attachment>(`/api/threads/${threadId}/uploads`, {
      method: "POST",
      body: form
    });
  }
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

export async function downloadArtifact(
  threadId: string,
  artifactId: string,
  fileName: string
): Promise<void> {
  // Ask the backend to mint a fresh download URL on demand (never stored, never stale).
  const { url } = await request<{ url: string }>(
    `/api/threads/${threadId}/artifacts/${artifactId}/download`
  );

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
