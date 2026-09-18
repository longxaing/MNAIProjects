export interface SharedImage { id: string; name: string }
export interface SharedMessage { role: "user" | "assistant"; content: string; images: SharedImage[] }
export interface SharedSnapshot { title: string; createdAt: string; expiresAt: string; messages: SharedMessage[] }
export interface SharePreview { previewId: string; snapshot: SharedSnapshot }
export interface ShareListItem { id: string; state: string; createdAt: string; expiresAt: string }

const BASE = import.meta.env.VITE_API_BASE?.trim() || "";
export const sharedPath = (threadId: string) => `${BASE}/api/shared/${encodeURIComponent(threadId)}`;

export async function readSharedConversation(threadId: string, signal: AbortSignal): Promise<SharedSnapshot> {
  const response = await fetch(sharedPath(threadId), { credentials: "omit", cache: "no-store", referrerPolicy: "no-referrer", signal });
  if (!response.ok) throw new Error(response.status === 429 ? "Too many requests. Please try again shortly." : "This shared conversation is unavailable, expired, or revoked.");
  return await response.json() as SharedSnapshot;
}

export async function readSharedImage(threadId: string, imageId: string, signal: AbortSignal): Promise<Blob> {
  const response = await fetch(`${sharedPath(threadId)}/images/${encodeURIComponent(imageId)}`,
    { credentials: "omit", cache: "no-store", referrerPolicy: "no-referrer", signal });
  if (!response.ok) throw new Error("Screenshot unavailable.");
  if (!response.headers.get("Content-Type")?.startsWith("image/png")) throw new Error("Unsupported screenshot format.");
  return response.blob();
}

export function shareUrl(threadId: string): string {
  return `${location.origin}${location.pathname}#/share/${encodeURIComponent(threadId)}`;
}