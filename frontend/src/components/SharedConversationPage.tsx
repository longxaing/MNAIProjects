import { useEffect, useState } from "react";
import { readSharedConversation, readSharedImage, type SharedImage, type SharedSnapshot } from "../api/sharing";
import AgentAvatar from "./AgentAvatar";
import SharedMarkdown from "./SharedMarkdown";
import "./sharing.css";

export type ShareImageLoader = (imageId: string, signal: AbortSignal) => Promise<Blob>;
export type ShareImageStatus = (imageId: string, ready: boolean) => void;

function SnapshotImage({ image, load, onStatus }: { image: SharedImage; load: ShareImageLoader; onStatus?: ShareImageStatus }) {
  const [url, setUrl] = useState("");
  const [error, setError] = useState("");
  useEffect(() => {
    const abort = new AbortController();
    let objectUrl = "";
    setUrl(""); setError("");
    onStatus?.(image.id, false);
    void load(image.id, abort.signal).then(async blob => {
      if (abort.signal.aborted) return;
      objectUrl = URL.createObjectURL(blob);
      const decoded = new Image();
      decoded.src = objectUrl;
      await decoded.decode();
      if (abort.signal.aborted) return;
      setUrl(objectUrl);
    }).catch(() => { if (!abort.signal.aborted) { setError("Screenshot unavailable."); onStatus?.(image.id, false); } });
    return () => { abort.abort(); if (objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [image.id, load, onStatus]);
  return <figure className="shared-image">{url ? <img src={url} alt={image.name} onLoad={() => onStatus?.(image.id, true)}
    onError={() => { setUrl(""); setError("Screenshot unavailable."); onStatus?.(image.id, false); }} /> : <p role="status">{error || "Loading screenshot..."}</p>}
    <figcaption>{image.name}</figcaption></figure>;
}

export function SharedSnapshotView({ snapshot, loadImage, onImageStatus }: { snapshot: SharedSnapshot; loadImage: ShareImageLoader; onImageStatus?: ShareImageStatus }) {
  return <>
    <header className="shared-heading"><p className="shared-label">Shared snapshot · Read-only</p>
      <h1>{snapshot.title}</h1><p>Captured {new Date(snapshot.createdAt).toLocaleString()} · Expires {new Date(snapshot.expiresAt).toLocaleString()}</p></header>
    <div className="shared-messages">{snapshot.messages.map((message, index) => <article key={index} className={`shared-message ${message.role}`}>
      <header>{message.role === "assistant" ? <AgentAvatar /> : null}<strong>{message.role === "user" ? "User" : "AzurePilot"}</strong></header>
      <SharedMarkdown content={message.content} />
      {message.images.map(image => <SnapshotImage key={image.id} image={image} load={loadImage} onStatus={onImageStatus} />)}
    </article>)}</div>
  </>;
}

export default function SharedConversationPage({ threadId }: { threadId: string }) {
  const [snapshot, setSnapshot] = useState<SharedSnapshot | null>(null);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState(0);
  const [loadImage] = useState<ShareImageLoader>(() => (imageId: string, signal: AbortSignal) => readSharedImage(threadId, imageId, signal));
  useEffect(() => {
    const priorTitle = document.title;
    document.title = "Shared conversation | AzurePilot";
    const robots = document.createElement("meta"); robots.name = "robots"; robots.content = "noindex,nofollow,noarchive";
    const referrer = document.createElement("meta"); referrer.name = "referrer"; referrer.content = "no-referrer";
    document.head.append(robots, referrer);
    return () => { robots.remove(); referrer.remove(); document.title = priorTitle; };
  }, []);
  useEffect(() => {
    const abort = new AbortController();
    setSnapshot(null); setError("");
    if (!/^[a-zA-Z0-9_-]{1,128}$/.test(threadId)) { setError("This shared conversation is unavailable."); return; }
    void readSharedConversation(threadId, abort.signal).then(result => {
      if (new Date(result.expiresAt).getTime() <= Date.now()) throw new Error("This shared conversation has expired.");
      setSnapshot(result);
    }).catch(reason => { if (!abort.signal.aborted) setError(reason instanceof Error ? reason.message : "Unable to load shared conversation."); });
    return () => abort.abort();
  }, [threadId, attempt]);
  useEffect(() => {
    if (!snapshot) return;
    const timer = window.setInterval(() => {
      if (new Date(snapshot.expiresAt).getTime() <= Date.now()) { setSnapshot(null); setError("This shared conversation has expired."); }
    }, 1000);
    return () => clearInterval(timer);
  }, [snapshot]);
  return <main className="shared-page"><div className="shared-inner">
    <div className="shared-brand"><AgentAvatar /><strong>AzurePilot</strong></div>
    {error ? <section className="shared-error" role="alert"><h1>Shared conversation unavailable</h1><p>{error}</p>
      <button type="button" onClick={() => setAttempt(value => value + 1)}>Try again</button></section>
      : snapshot ? <SharedSnapshotView snapshot={snapshot} loadImage={loadImage} /> : <p role="status">Loading shared conversation...</p>}
  </div></main>;
}