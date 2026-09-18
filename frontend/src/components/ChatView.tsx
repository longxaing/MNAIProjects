import { useState } from "react";
import { useChat } from "../store/chat";
import Composer from "./Composer";
import MessageList from "./MessageList";
import ShareDialog from "./ShareDialog";
import { Share2 } from "lucide-react";

export default function ChatView() {
  const [input, setInput] = useState("");
  const [sharing, setSharing] = useState(false);
  const messages = useChat((s) => s.messages);
  const error = useChat((s) => s.error);
  const dismissError = useChat((s) => s.dismissError);
  const threads = useChat((s) => s.threads);
  const currentThreadId = useChat((s) => s.currentThreadId);

  const title = threads.find((t) => t.id === currentThreadId)?.title ?? "New conversation";

  return (
    <main className="chat">
      <header className="chat-header">
        <h2>{title}</h2>
        <span className="tag">Azure</span>
        <button type="button" className="share-trigger" title="Share conversation" aria-label="Share conversation"
          disabled={!currentThreadId} onClick={() => setSharing(true)}><Share2 size={18} /></button>
      </header>

      {error && (
        <div className="error-banner">
          <span>{error}</span>
          <button onClick={dismissError}>Dismiss</button>
        </div>
      )}

      <MessageList onExample={setInput} />
      <Composer value={input} onChange={setInput} />
      {sharing && currentThreadId && <ShareDialog key={currentThreadId} threadId={currentThreadId}
        screenshots={messages.filter(message => !message.streaming).flatMap(message => message.artifacts)
          .filter((artifact, index, all) => artifact.kind === "uiScreenshot" && all.findIndex(item => item.id === artifact.id) === index)}
        onClose={() => setSharing(false)} />}
    </main>
  );
}
