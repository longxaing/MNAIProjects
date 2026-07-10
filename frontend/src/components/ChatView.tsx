import { useState } from "react";
import { useChat } from "../store/chat";
import Composer from "./Composer";
import MessageList from "./MessageList";

export default function ChatView() {
  const [input, setInput] = useState("");
  const error = useChat((s) => s.error);
  const dismissError = useChat((s) => s.dismissError);
  const threads = useChat((s) => s.threads);
  const currentThreadId = useChat((s) => s.currentThreadId);

  const title = threads.find((t) => t.id === currentThreadId)?.title ?? "New conversation";

  return (
    <main className="chat">
      <header className="chat-header">
        <h2>{title}</h2>
        <span className="tag">DOCX · PPTX</span>
      </header>

      {error && (
        <div className="error-banner">
          <span>{error}</span>
          <button onClick={dismissError}>Dismiss</button>
        </div>
      )}

      <MessageList onExample={setInput} />
      <Composer value={input} onChange={setInput} />
    </main>
  );
}
