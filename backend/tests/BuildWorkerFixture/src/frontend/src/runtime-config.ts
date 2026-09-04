declare global {
  interface Window {
    __APP_CONFIG__?: {
      apiBaseUrl?: string;
    };
  }
}

export function getApiBaseUrl(): string {
  const value = window.__APP_CONFIG__?.apiBaseUrl?.trim();
  if (!value) {
    throw new Error("The deployment runtime configuration is missing apiBaseUrl.");
  }
  return value.replace(/\/$/, "");
}