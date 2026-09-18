import { useEffect, useRef, useState } from "react";
import { Copy, Eye, Link2, ShieldAlert, Trash2, X } from "lucide-react";
import { api } from "../api/client";
import { shareUrl, type ShareListItem, type SharePreview } from "../api/sharing";
import type { Artifact } from "../api/types";
import { SharedSnapshotView, type ShareImageLoader, type ShareImageStatus } from "./SharedConversationPage";
import "./sharing.css";

function PreviewContent({ threadId, preview, onImageStatus }: { threadId: string; preview: SharePreview; onImageStatus: ShareImageStatus }) {
  const [load] = useState<ShareImageLoader>(() => (imageId: string, signal: AbortSignal) =>
    api.previewShareImage(threadId, preview.previewId, imageId, signal));
  return <SharedSnapshotView snapshot={preview.snapshot} loadImage={load} onImageStatus={onImageStatus} />;
}

export default function ShareDialog({ threadId, screenshots, onClose }: {
  threadId: string; screenshots: Artifact[]; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [days, setDays] = useState(7);
  const [selected, setSelected] = useState<string[]>([]);
  const [preview, setPreview] = useState<SharePreview | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [current, setCurrent] = useState<ShareListItem | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [uncertain, setUncertain] = useState(false);
  const [readyImages, setReadyImages] = useState<Record<string, boolean>>({});
  const [onImageStatus] = useState<ShareImageStatus>(() => (imageId: string, ready: boolean) => {
    setReadyImages(previous => ({ ...previous, [imageId]: ready }));
    if (!ready) setReviewed(false);
  });
  const imagesReady = preview?.snapshot.messages.every(message => message.images.every(image => readyImages[image.id])) ?? false;
  const link = shareUrl(threadId);
  const active = current?.state === "active" && Date.parse(current.expiresAt) > Date.now();
  useEffect(() => {
    const opener = document.activeElement;
    const modal = dialog.current;
    modal?.showModal();
    let disposed = false;
    void api.listShares(threadId).then(items => { if (!disposed) setCurrent(items[0] ?? null); })
      .catch(reason => { if (!disposed) setError(reason instanceof Error ? reason.message : "Unable to load sharing settings."); })
      .finally(() => { if (!disposed) setBusy(false); });
    return () => { disposed = true; modal?.close(); if (opener instanceof HTMLElement) opener.focus(); };
  }, [threadId]);
  function resetPreview() { setPreview(null); setReadyImages({}); setReviewed(false); setNotice(""); }
  async function execute(action: () => Promise<void>) {
    setBusy(true); setError(""); setNotice("");
    try { await action(); } catch (reason) { setError(reason instanceof Error ? reason.message : "Sharing failed."); }
    finally { setBusy(false); }
  }
  return <dialog className="share-dialog" ref={dialog} aria-labelledby="share-dialog-title" onKeyDown={event => {
    if (event.key === "Escape") { event.preventDefault(); if (!busy) onClose(); }
  }} onCancel={event => {
    if (busy) event.preventDefault(); else onClose();
  }}>
    <header className="share-dialog-header"><h2 id="share-dialog-title">Share conversation</h2>
      <button type="button" className="share-icon" title="Close" aria-label="Close sharing settings" disabled={busy} onClick={onClose}><X size={19} /></button></header>
    <div className="share-dialog-body">
      <p className="share-warning"><ShieldAlert size={20} aria-hidden="true" /> Anyone who knows this conversation ID can view the published snapshot without signing in. Republish restores the same link. Visible content can be copied or captured.</p>
      <p className="share-muted">Only completed messages are included. Tool output and file downloads are excluded; links and common sensitive values are removed. Review every message and selected screenshot for information that automatic checks may miss.</p>
      {current && <section className="share-current" aria-label="Current share">
        <strong>{uncertain ? "Status unconfirmed" : active ? "Published" : current.state === "revoked" ? "Revoked" : "Expired"}</strong>
        <span>Expires {new Date(current.expiresAt).toLocaleString()}</span>
        {active && <><label htmlFor="share-link">Public link</label><div className="share-link-row">
          <input id="share-link" readOnly value={link} onFocus={event => event.target.select()} />
          <button className="share-icon" title="Copy link" aria-label="Copy share link" onClick={() => void execute(async () => {
            await navigator.clipboard.writeText(link); setNotice("Link copied.");
          })} disabled={busy}><Copy size={18} /></button>
        </div><button className="share-danger" disabled={busy} onClick={() => void execute(async () => {
          await api.revokeShare(threadId, current.id); resetPreview(); setCurrent({ ...current, state: "revoked" }); setUncertain(false); setNotice("Link revoked.");
        })}><Trash2 size={16} /> Revoke link</button></>}
      </section>}
      <div className="share-options"><label htmlFor="share-days">Expires after</label>
        <select id="share-days" value={days} disabled={busy} onChange={event => { setDays(Number(event.target.value)); resetPreview(); }}>
          <option value={1}>1 day</option><option value={7}>7 days</option><option value={30}>30 days</option>
        </select></div>
      <fieldset className="share-screenshots" disabled={busy}><legend>UI screenshots (optional, up to 6)</legend>
        <p className="share-muted">Images are copied as-is, not automatically redacted.</p>
        {screenshots.length === 0 ? <p>No UI screenshots available.</p> : screenshots.map(image => <label key={image.id}>
          <input type="checkbox" checked={selected.includes(image.id)} disabled={image.sizeBytes > 2 * 1024 * 1024 || selected.length >= 6 && !selected.includes(image.id)}
            onChange={event => { setSelected(event.target.checked ? [...selected, image.id] : selected.filter(id => id !== image.id)); resetPreview(); }} />
          <span>{image.fileName}{image.sizeBytes > 2 * 1024 * 1024 ? " (exceeds 2 MB)" : ""}</span>
        </label>)}
      </fieldset>
      {uncertain && <section role="alert"><p>Publication could not be confirmed. This snapshot may already be public. Check its status before trying again.</p>
        <button className="share-action" disabled={busy} onClick={() => void execute(async () => {
          setCurrent((await api.listShares(threadId))[0] ?? null); setUncertain(false);
        })}>Check publication status</button></section>}
      <button className="share-action" disabled={busy || uncertain} onClick={() => void execute(async () => {
        resetPreview(); setPreview(await api.previewShare(threadId, days, selected));
      })}><Eye size={17} /> {busy ? "Working..." : "Create preview"}</button>
      {error && <p className="share-error" role="alert">{error}</p>}
      {notice && <p role="status" className="share-notice">{notice}</p>}
      {preview && <>
        <div className="share-preview" tabIndex={0} aria-label="Public snapshot preview"><PreviewContent key={preview.previewId} threadId={threadId} preview={preview} onImageStatus={onImageStatus} /></div>
        {!imagesReady && <p role="status">All selected screenshots must display successfully before publication. Create a new preview to retry failed images.</p>}
        <label className="share-confirm"><input type="checkbox" checked={reviewed} disabled={busy || !imagesReady} onChange={event => setReviewed(event.target.checked)} />
          <span>I reviewed this exact preview and all selected images. They are safe to publish without authentication.</span></label>
        <button className="share-publish" disabled={!reviewed || busy || !imagesReady} onClick={() => void execute(async () => {
          try {
            const published = await api.publishShare(threadId, preview.previewId);
            setCurrent({ id: published.id, state: "active", createdAt: preview.snapshot.createdAt, expiresAt: preview.snapshot.expiresAt });
            setNotice("Snapshot published. The link stays unchanged.");
          } catch (reason) { setUncertain(true); throw reason; }
          finally { setPreview(null); setReviewed(false); }
        })}><Link2 size={17} /> {active ? "Update shared snapshot" : "Publish snapshot"}</button>
      </>}
    </div>
  </dialog>;
}