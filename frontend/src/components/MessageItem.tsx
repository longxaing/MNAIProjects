import type { Message } from "../api/types";
import { useChat } from "../store/chat";
import ArtifactCard from "./ArtifactCard";
import MarkdownContent from "./MarkdownContent";

function attachmentIcon(kind: string): string {
  switch (kind) {
    case "image":
      return "🖼️";
    case "pdf":
      return "📄";
    case "docx":
      return "📝";
    case "pptx":
      return "📊";
    default:
      return "📎";
  }
}

export default function MessageItem({ message }: { message: Message }) {
  const threadId = useChat((s) => s.currentThreadId) ?? message.threadId;

  if (message.role === "tool") {
    if (message.artifacts.length === 0) return null;
    return (
      <div className="row assistant">
        <div className="avatar bot">MW</div>
        <div className="bubble tool-bubble">
          {message.artifacts.map((a) => (
            <ArtifactCard key={a.id} artifact={a} threadId={threadId} />
          ))}
        </div>
      </div>
    );
  }

  if (message.role === "system") {
    return <div className="system-note">{message.content}</div>;
  }

  const isUser = message.role === "user";

  // Skip empty assistant turns (e.g. a round that only issued a tool call and produced
  // no text): once finished with no content and no artifacts there is nothing to show.
  if (
    !isUser &&
    !message.streaming &&
    message.content.trim().length === 0 &&
    message.artifacts.length === 0
  ) {
    return null;
  }

  return (
    <div className={`row ${isUser ? "user" : "assistant"}`}>
      {!isUser && <div className="avatar bot">MW</div>}
      <div className={`bubble ${isUser ? "user-bubble" : "assistant-bubble"}`}>
        <div className="content">
          <MarkdownContent content={message.content} renderMermaid={!message.streaming} />
          {message.streaming && message.content.length === 0 ? (
            <span className="typing">
              <span></span>
              <span></span>
              <span></span>
            </span>
          ) : (
            message.streaming && <span className="caret" />
          )}
        </div>
        {message.attachments.length > 0 && (
          <div className="msg-attachments">
            {message.attachments.map((a) => (
              <span key={a.id} className="msg-attachment" title={a.fileName}>
                <span className="attach-ico">{attachmentIcon(a.kind)}</span>
                <span className="attach-name">{a.fileName}</span>
              </span>
            ))}
          </div>
        )}
        {message.artifacts.map((a) => (
          <ArtifactCard key={a.id} artifact={a} threadId={threadId} />
        ))}
      </div>
      {isUser && <div className="avatar me">You</div>}
    </div>
  );
}
