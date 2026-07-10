/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE?: string;
  readonly VITE_AAD_CLIENT_ID?: string;
  readonly VITE_AAD_TENANT?: string;
  readonly VITE_API_SCOPE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
