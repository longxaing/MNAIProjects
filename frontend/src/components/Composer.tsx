import { useEffect, useRef, type KeyboardEvent } from "react";
import { useChat } from "../store/chat";

export default function Composer({
  value,
  onChange
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  const send = useChat((s) => s.send);
  const sending = useChat((s) => s.sending);
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [value]);

  async function submit() {
    const text = value.trim();
    if (!text || sending) return;
    onChange("");
    await send(text);
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  }

  return (
    <div className="composer">
      <textarea
        ref={ref}
        value={value}
        rows={1}
        placeholder="Describe the document or deck you want…"
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
      />
      <button className="send" onClick={() => void submit()} disabled={sending || !value.trim()}>
        {sending ? "Working…" : "Send"}
      </button>
    </div>
  );
}
