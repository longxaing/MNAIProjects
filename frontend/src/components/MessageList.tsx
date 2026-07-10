import { useEffect, useRef } from "react";
import { useChat } from "../store/chat";
import MessageItem from "./MessageItem";

const EXAMPLES = [
  "Create a 6-slide pitch deck for a coffee subscription startup.",
  "Write a one-page project brief for a mobile budgeting app.",
  "Make a presentation explaining the water cycle for 5th graders.",
  "Draft a formal quarterly business review document with a summary table."
];

export default function MessageList({ onExample }: { onExample: (text: string) => void }) {
  const messages = useChat((s) => s.messages);
  const toolActivity = useChat((s) => s.toolActivity);
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, toolActivity]);

  if (messages.length === 0) {
    return (
      <div className="empty">
        <div className="empty-mark">MW</div>
        <h1>What should we create?</h1>
        <p>
          Describe a document or presentation and MnaiWork will generate a polished{" "}
          <b>.docx</b> or <b>.pptx</b> for you.
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
        <MessageItem key={m.id} message={m} />
      ))}
      {toolActivity && (
        <div className="row assistant">
          <div className="avatar bot">MW</div>
          <div className="bubble tool-activity">
            <span className="spinner" /> {toolActivity}
          </div>
        </div>
      )}
      <div ref={endRef} />
    </div>
  );
}
