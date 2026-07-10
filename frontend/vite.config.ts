import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The dev server proxies /api to the ASP.NET Core backend (http profile).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: process.env.VITE_API_PROXY ?? "http://localhost:5124",
        changeOrigin: true
      }
    }
  }
});
