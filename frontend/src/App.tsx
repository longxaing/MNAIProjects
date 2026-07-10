import { useEffect, useRef, useState } from "react";
import { api } from "./api/client";
import { authEnabled, currentAccount, initAuth, login } from "./auth/auth";
import ChatView from "./components/ChatView";
import Sidebar from "./components/Sidebar";
import { useChat } from "./store/chat";

/** Register/refresh the user record on the backend. Best-effort; never blocks the app. */
async function bootstrapUser(): Promise<void> {
  try {
    await api.getMe();
  } catch (err) {
    console.warn("Failed to register user:", err);
  }
}

export default function App() {
  const [ready, setReady] = useState(false);
  const [authed, setAuthed] = useState(!authEnabled);
  const init = useChat((s) => s.init);
  // Guard against React 18 StrictMode running the bootstrap effect twice in dev,
  // which would otherwise double every startup API call.
  const bootstrapped = useRef(false);

  useEffect(() => {
    if (bootstrapped.current) return;
    bootstrapped.current = true;

    void (async () => {
      await initAuth();
      const ok = !authEnabled || Boolean(currentAccount());
      setAuthed(ok);
      if (ok) {
        await bootstrapUser();
        await init();
      }
      setReady(true);
    })();
  }, [init]);

  async function handleSignIn() {
    await login();
    if (currentAccount()) {
      setAuthed(true);
      await bootstrapUser();
      await init();
    }
  }

  if (!ready) {
    return (
      <div className="app-loading">
        <div className="loading-brand">
          <div className="brand-mark big pulse">MW</div>
          <div className="loading-title">MnaiWork</div>
          <div className="loading-dots">
            <span></span>
            <span></span>
            <span></span>
          </div>
        </div>
      </div>
    );
  }

  if (!authed) {
    return (
      <div className="login-screen">
        <div className="login-card">
          <div className="brand-mark big">MW</div>
          <h1>MnaiWork</h1>
          <p>Generate polished Word documents and PowerPoint decks from a chat.</p>
          <button className="signin" onClick={() => void handleSignIn()}>
            Sign in with Microsoft
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="app">
      <Sidebar />
      <ChatView />
    </div>
  );
}
