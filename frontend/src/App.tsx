import { useEffect, useState } from "react";
import { authEnabled, currentAccount, initAuth, login } from "./auth/auth";
import ChatView from "./components/ChatView";
import Sidebar from "./components/Sidebar";
import { useChat } from "./store/chat";

export default function App() {
  const [ready, setReady] = useState(false);
  const [authed, setAuthed] = useState(!authEnabled);
  const init = useChat((s) => s.init);

  useEffect(() => {
    void (async () => {
      await initAuth();
      const ok = !authEnabled || Boolean(currentAccount());
      setAuthed(ok);
      if (ok) await init();
      setReady(true);
    })();
  }, [init]);

  async function handleSignIn() {
    await login();
    if (currentAccount()) {
      setAuthed(true);
      await init();
    }
  }

  if (!ready) {
    return <div className="app-loading">Loading MnaiWork…</div>;
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
