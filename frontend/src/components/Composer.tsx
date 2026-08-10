import { useEffect, useRef, type ChangeEvent, type KeyboardEvent } from "react";
import { useChat } from "../store/chat";

const ACCEPT = ".png,.jpg,.jpeg,.gif,.bmp,.webp,.pdf,.docx,.pptx,image/*,application/pdf";

function kindIcon(kind: string): string {
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

export default function Composer({
  value,
  onChange
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  const send = useChat((s) => s.send);
  const sending = useChat((s) => s.sending);
  const uploading = useChat((s) => s.uploading);
  const pending = useChat((s) => s.pendingAttachments);
  const uploadFiles = useChat((s) => s.uploadFiles);
  const removeAttachment = useChat((s) => s.removePendingAttachment);
  const ref = useRef<HTMLTextAreaElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [value]);

  const canSend = (value.trim().length > 0 || pending.length > 0) && !sending && !uploading;

  async function submit() {
    if (!canSend) return;
    const text = value;
    onChange("");
    await send(text);
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      void submit();
    }
  }

  async function onFilesPicked(e: ChangeEvent<HTMLInputElement>) {
    const files = e.target.files;
    if (files && files.length > 0) {
      await uploadFiles(files);
    }
    if (fileRef.current) fileRef.current.value = "";
  }

  return (
    <div className="composer-wrap">
      {(pending.length > 0 || uploading) && (
        <div className="attach-tray">
          {pending.map((a) => (
            <div key={a.id} className="attach-chip" title={a.fileName}>
              <span className="attach-ico">{kindIcon(a.kind)}</span>
              <span className="attach-name">{a.fileName}</span>
              <button className="attach-x" onClick={() => removeAttachment(a.id)} title="Remove">
                ×
              </button>
            </div>
          ))}
          {uploading && <div className="attach-chip uploading">Uploading…</div>}
        </div>
      )}

      <div className="composer">
        <input
          ref={fileRef}
          type="file"
          accept={ACCEPT}
          multiple
          hidden
          onChange={(e) => void onFilesPicked(e)}
        />
        <button
          className="attach-btn"
          title="Attach image, PDF, DOCX or PPTX"
          onClick={() => fileRef.current?.click()}
          disabled={uploading}
        >
          +
        </button>
        <textarea
          ref={ref}
          value={value}
          rows={1}
          placeholder="Describe the document or deck you want…"
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={onKeyDown}
        />
        <button className="send" onClick={() => void submit()} disabled={!canSend}>
          {sending ? "Working…" : "Send"}
        </button>
      </div>
    </div>
  );
}
