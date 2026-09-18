import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import ShareDialog from "../src/components/ShareDialog";
import { api } from "../src/api/client";
import type { SharePreview } from "../src/api/sharing";

vi.mock("../src/api/client", () => ({ api: {
  listShares: vi.fn(), previewShare: vi.fn(), publishShare: vi.fn(), revokeShare: vi.fn(), previewShareImage: vi.fn()
} }));

const published = { id: "thread-test", state: "active", createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() };
const preview: SharePreview = { previewId: "preview", snapshot: { title: "Reviewed content", createdAt: published.createdAt,
  expiresAt: published.expiresAt, messages: [{ role: "assistant", content: "Safe text", images: [] }] } };
const decode = vi.fn();

beforeEach(() => {
  vi.resetAllMocks();
  Object.defineProperty(HTMLDialogElement.prototype, "showModal", { configurable: true, value() { this.setAttribute("open", ""); } });
  Object.defineProperty(HTMLDialogElement.prototype, "close", { configurable: true, value() { this.removeAttribute("open"); } });
  Object.defineProperty(HTMLImageElement.prototype, "decode", { configurable: true, value: decode });
  Object.defineProperty(URL, "createObjectURL", { configurable: true, value: vi.fn(() => "blob:test") });
  Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
  decode.mockResolvedValue(undefined);
  vi.mocked(api.listShares).mockResolvedValue([]);
  vi.mocked(api.previewShare).mockResolvedValue(preview);
  vi.mocked(api.publishShare).mockResolvedValue({ id: published.id, path: "#/share/test" });
  vi.mocked(api.revokeShare).mockResolvedValue(undefined);
  vi.mocked(api.previewShareImage).mockResolvedValue(new Blob(["image"], { type: "image/png" }));
});
afterEach(cleanup);

async function openPreview(withImage = false) {
  if (withImage) vi.mocked(api.previewShare).mockResolvedValue({ ...preview, snapshot: { ...preview.snapshot,
    messages: [{ role: "assistant", content: "Safe text", images: [{ id: "image", name: "UI screenshot 1" }] }] } });
  const user = userEvent.setup();
  render(<ShareDialog threadId="test" screenshots={[]} onClose={vi.fn()} />);
  await waitFor(() => expect((screen.getByRole("button", { name: "Create preview" }) as HTMLButtonElement).disabled).toBe(false));
  await user.click(screen.getByRole("button", { name: "Create preview" }));
  await screen.findByRole("heading", { name: "Reviewed content" });
  return user;
}

describe("sharing privacy and publication recovery", () => {
  it("keeps the link and revoke action after publication without a follow-up GET", async () => {
    vi.mocked(api.listShares).mockResolvedValueOnce([]).mockRejectedValue(new Error("Refresh failed"));
    const user = await openPreview();
    await user.click(screen.getByRole("checkbox"));
    await user.click(screen.getByRole("button", { name: "Publish snapshot" }));
    await screen.findByText("Snapshot published. The link stays unchanged.");
    expect(screen.getByRole("textbox", { name: "Public link" })).toBeTruthy();
    expect(api.listShares).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole("button", { name: "Revoke link" }));
    await screen.findByText("Link revoked.");
    expect(api.revokeShare).toHaveBeenCalledWith("test", published.id);
    expect(api.listShares).toHaveBeenCalledTimes(1);
  });

  it("requires reconciliation after an uncertain publish response", async () => {
    vi.mocked(api.publishShare).mockRejectedValue(new Error("Response lost"));
    vi.mocked(api.listShares).mockResolvedValueOnce([{ ...published, state: "revoked" }]).mockResolvedValue([published]);
    const user = await openPreview();
    await user.click(screen.getByRole("checkbox"));
    await user.click(screen.getByRole("button", { name: "Publish snapshot" }));
    await screen.findByText(/This snapshot may already be public/);
    expect(screen.getByText("Status unconfirmed")).toBeTruthy();
    expect(screen.queryByText("Revoked")).toBeNull();
    expect((screen.getByRole("button", { name: "Create preview" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Publish snapshot" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Check publication status" }));
    await screen.findByRole("button", { name: "Revoke link" });
    expect(api.publishShare).toHaveBeenCalledTimes(1);
  });

  it("blocks publication when the screenshot request fails", async () => {
    vi.mocked(api.previewShareImage).mockRejectedValue(new Error("Unavailable"));
    await openPreview(true);
    await screen.findByText(/Screenshot unavailable/);
    expect((screen.getByRole("checkbox") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Publish snapshot" }) as HTMLButtonElement).disabled).toBe(true);
    expect(api.publishShare).not.toHaveBeenCalled();
  });

  it("blocks publication when PNG bytes cannot be decoded", async () => {
    decode.mockRejectedValue(new Error("Invalid PNG"));
    await openPreview(true);
    await screen.findByText(/Screenshot unavailable/);
    expect((screen.getByRole("checkbox") as HTMLInputElement).disabled).toBe(true);
    expect(api.publishShare).not.toHaveBeenCalled();
  });

  it("requires rendered images and resets confirmation when options change", async () => {
    const user = await openPreview(true);
    const image = await screen.findByRole("img", { name: "UI screenshot 1" });
    expect((screen.getByRole("checkbox") as HTMLInputElement).disabled).toBe(true);
    fireEvent.load(image);
    await waitFor(() => expect((screen.getByRole("checkbox") as HTMLInputElement).disabled).toBe(false));
    await user.click(screen.getByRole("checkbox"));
    expect((screen.getByRole("button", { name: "Publish snapshot" }) as HTMLButtonElement).disabled).toBe(false);
    await user.selectOptions(screen.getByRole("combobox"), "1");
    expect(screen.queryByRole("button", { name: "Publish snapshot" })).toBeNull();
    expect(api.publishShare).not.toHaveBeenCalled();
  });
});