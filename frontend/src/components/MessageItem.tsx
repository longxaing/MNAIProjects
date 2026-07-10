import type { Message } from "../api/types";
import ArtifactCard from "./ArtifactCard";

export default function MessageItem({ message }: { message: Message }) {
  if (message.role === "tool") {
    if (message.artifacts.length === 0) return null;
    return (
      <div className="row assistant">
        <div className="avatar bot">MW</div>
        <div className="bubble tool-bubble">
          {message.artifacts.map((a) => (
            <ArtifactCard key={a.id} artifact={a} />
          ))}
        </div>
      </div>
    );
  }

  if (message.role === "system") {
    return <div className="system-note">{message.content}</div>;
  }

  const isUser = message.role === "user";
  return (
    <div className={`row ${isUser ? "user" : "assistant"}`}>
      {!isUser && <div className="avatar bot">MW</div>}
      <div className={`bubble ${isUser ? "user-bubble" : "assistant-bubble"}`}>
        <div className="content">
          {message.content}
          {message.streaming && <span className="caret" />}
        </div>
        {message.artifacts.map((a) => (
          <ArtifactCard key={a.id} artifact={a} />
        ))}
      </div>
      {isUser && <div className="avatar me">You</div>}
    </div>
  );
}
