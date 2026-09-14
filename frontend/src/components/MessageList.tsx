import { useEffect, useRef } from "react";
import { useChat } from "../store/chat";
import MessageItem from "./MessageItem";
import AgentAvatar from "./AgentAvatar";

const EXAMPLES = [
  "Build a text blog with a React frontend and an ASP.NET Core API on Azure.",
  "Create a task tracker with Cosmos DB persistence.",
  "Improve the UI of the application in this conversation.",
  "Review the latest build failure and repair the application."
];

export default function MessageList({ onExample }: { onExample: (text: string) => void }) {
  const messages = useChat((s) => s.messages);
  const toolActivity = useChat((s) => s.toolActivity);
  const endRef = useRef<HTMLDivElement>(null);
  const latestSourceZipByName = new Map<string, string>();
  for (const message of messages) {
    for (const artifact of message.artifacts) {
      if (artifact.kind === "sourceZip") latestSourceZipByName.set(artifact.fileName, artifact.id);
    }
  }
  const latestSourceZipIds = new Set(latestSourceZipByName.values());

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "auto" });
  }, [messages, toolActivity]);

  if (messages.length === 0) {
    return (
      <div className="empty">
        <AgentAvatar className="empty-mark" />
        <h1>AzurePilot</h1>
        <p>
          What are we building today?
        </p>
        <div className="examples">
          {EXAMPLES.map((e) => (
            <button key={e} onClick={() => onExample(e)}>
              {e}
            </button>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="messages">
      {messages.map((m) => (
        <MessageItem key={m.id} message={m} latestSourceZipIds={latestSourceZipIds} />
      ))}
      {toolActivity && (
        <div className="row assistant">
          <AgentAvatar />
          <div className="bubble tool-activity">
            <span className="spinner" /> {toolActivity}
          </div>
        </div>
      )}
      <div ref={endRef} />
    </div>
  );
}
