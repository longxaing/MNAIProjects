import { create } from "zustand";
import { api, streamRun } from "../api/client";
import type { AgentEvent, Artifact, Attachment, Message, ThreadListItem } from "../api/types";

interface ChatState {
  threads: ThreadListItem[];
  currentThreadId: string | null;
  messages: Message[];
  sending: boolean;
  toolActivity: string | null;
  error: string | null;
  pendingAttachments: Attachment[];
  uploading: boolean;

  init: () => Promise<void>;
  refreshThreads: () => Promise<void>;
  openThread: (threadId: string) => Promise<void>;
  newThread: () => void;
  deleteThread: (threadId: string) => Promise<void>;
  send: (content: string) => Promise<void>;
  uploadFiles: (files: FileList | File[]) => Promise<void>;
  removePendingAttachment: (id: string) => void;
  dismissError: () => void;
}

let streamController: AbortController | null = null;

function nowIso() {
  return new Date().toISOString();
}

export const useChat = create<ChatState>((set, get) => {
  function apply(event: AgentEvent) {
    switch (event.type) {
      case "message": {
        if (!event.messageId) break;
        const stub: Message = {
          id: event.messageId,
          threadId: get().currentThreadId ?? "",
          role: event.role ?? "assistant",
          content: "",
          sequence: get().messages.length + 1,
          artifacts: [],
          attachments: [],
          streaming: true,
          createdAt: nowIso(),
          updatedAt: nowIso()
        };
        set({ messages: [...get().messages, stub] });
        break;
      }
      case "delta": {
        set({
          messages: get().messages.map((m) =>
            m.id === event.messageId ? { ...m, content: m.content + (event.delta ?? "") } : m
          )
        });
        break;
      }
      case "message_done": {
        set({
          messages: get().messages.map((m) =>
            m.id === event.messageId
              ? { ...m, content: event.content ?? m.content, streaming: false }
              : m
          )
        });
        break;
      }
      case "tool": {
        const label =
          event.toolStatus === "started"
            ? `Generating with ${event.tool}…`
            : `${event.tool} ${event.toolStatus}`;
        set({ toolActivity: event.toolStatus === "completed" ? null : label });
        break;
      }
      case "artifact": {
        if (event.artifact) upsertArtifact(event.messageId, event.artifact);
        break;
      }
      case "error": {
        set({ error: event.error ?? "The agent run failed." });
        break;
      }
      default:
        break;
    }
  }

  function upsertArtifact(messageId: string | undefined, artifact: Artifact) {
    const messages = get().messages;
    const existing = messageId ? messages.find((m) => m.id === messageId) : undefined;
    if (existing) {
      set({
        messages: messages.map((m) =>
          m.id === messageId ? { ...m, artifacts: [...m.artifacts, artifact] } : m
        )
      });
      return;
    }
    const toolMessage: Message = {
      id: messageId ?? crypto.randomUUID(),
      threadId: get().currentThreadId ?? "",
      role: "tool",
      content: "",
      sequence: messages.length + 1,
      artifacts: [artifact],
      attachments: [],
      createdAt: nowIso(),
      updatedAt: nowIso()
    };
    set({ messages: [...messages, toolMessage] });
  }

  return {
    threads: [],
    currentThreadId: null,
    messages: [],
    sending: false,
    toolActivity: null,
    error: null,
    pendingAttachments: [],
    uploading: false,

    async init() {
      await get().refreshThreads();
      const first = get().threads[0];
      if (first) await get().openThread(first.id);
    },

    async refreshThreads() {
      const threads = await api.listThreads();
      set({ threads });
    },

    async openThread(threadId: string) {
      streamController?.abort();
      set({ currentThreadId: threadId, messages: [], error: null, toolActivity: null });
      const messages = await api.getMessages(threadId);
      if (get().currentThreadId === threadId) set({ messages });
    },

    newThread() {
      streamController?.abort();
      set({ currentThreadId: null, messages: [], error: null, toolActivity: null, pendingAttachments: [] });
    },

    async deleteThread(threadId: string) {
      await api.deleteThread(threadId);
      const threads = get().threads.filter((t) => t.id !== threadId);
      set({ threads });
      if (get().currentThreadId === threadId) {
        set({ currentThreadId: null, messages: [] });
        if (threads[0]) await get().openThread(threads[0].id);
      }
    },

    async uploadFiles(files: FileList | File[]) {
      const list = Array.from(files);
      if (list.length === 0 || get().uploading) return;

      // Uploads need a thread to attach to; create one lazily.
      let threadId = get().currentThreadId;
      if (!threadId) {
        const thread = await api.createThread();
        set({
          currentThreadId: thread.id,
          threads: [{ id: thread.id, title: thread.title, updatedAt: thread.updatedAt }, ...get().threads]
        });
        threadId = thread.id;
      }

      set({ uploading: true, error: null });
      try {
        for (const file of list) {
          const att = await api.uploadFile(threadId, file);
          set({ pendingAttachments: [...get().pendingAttachments, att] });
        }
      } catch (err) {
        set({ error: err instanceof Error ? err.message : "Failed to upload the file." });
      } finally {
        set({ uploading: false });
      }
    },

    removePendingAttachment(id: string) {
      set({ pendingAttachments: get().pendingAttachments.filter((a) => a.id !== id) });
    },

    async send(content: string) {
      const trimmed = content.trim();
      const attachments = get().pendingAttachments;
      if ((!trimmed && attachments.length === 0) || get().sending) return;

      let threadId = get().currentThreadId;
      if (!threadId) {
        const thread = await api.createThread();
        set({
          currentThreadId: thread.id,
          threads: [{ id: thread.id, title: thread.title, updatedAt: thread.updatedAt }, ...get().threads]
        });
        threadId = thread.id;
      }

      const optimistic: Message = {
        id: crypto.randomUUID(),
        threadId,
        role: "user",
        content: trimmed,
        sequence: get().messages.length + 1,
        artifacts: [],
        attachments,
        createdAt: nowIso(),
        updatedAt: nowIso()
      };
      set({
        messages: [...get().messages, optimistic],
        sending: true,
        error: null,
        toolActivity: null,
        pendingAttachments: []
      });

      streamController?.abort();
      const controller = new AbortController();
      streamController = controller;

      try {
        const { runId } = await api.sendMessage(threadId, trimmed, attachments);
        await streamRun(threadId, runId, apply, controller.signal);
      } catch (err) {
        if (!controller.signal.aborted) {
          set({ error: err instanceof Error ? err.message : "Failed to reach the agent." });
        }
      } finally {
        if (streamController === controller) streamController = null;
        set({ sending: false, toolActivity: null });
        // Reconcile with the authoritative persisted state.
        if (get().currentThreadId === threadId) {
          try {
            const messages = await api.getMessages(threadId);
            set({ messages });
          } catch {
            /* keep optimistic view */
          }
          await get().refreshThreads();
        }
      }
    },

    dismissError() {
      set({ error: null });
    }
  };
});
