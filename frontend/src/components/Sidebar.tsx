import { authEnabled, currentAccount, logout } from "../auth/auth";
import { useChat } from "../store/chat";

export default function Sidebar() {
  const threads = useChat((s) => s.threads);
  const currentThreadId = useChat((s) => s.currentThreadId);
  const openThread = useChat((s) => s.openThread);
  const newThread = useChat((s) => s.newThread);
  const deleteThread = useChat((s) => s.deleteThread);
  const account = currentAccount();

  return (
    <aside className="sidebar">
      <div className="brand">
        <div className="brand-mark">MW</div>
        <div className="brand-name">MnaiWork</div>
      </div>

      <button className="new-chat" onClick={newThread}>
        + New conversation
      </button>

      <nav className="threads">
        {threads.map((t) => (
          <div
            key={t.id}
            className={`thread ${t.id === currentThreadId ? "active" : ""}`}
            onClick={() => void openThread(t.id)}
          >
            <span className="thread-title">{t.title}</span>
            <button
              className="thread-del"
              title="Delete conversation"
              onClick={(e) => {
                e.stopPropagation();
                void deleteThread(t.id);
              }}
            >
              ×
            </button>
          </div>
        ))}
        {threads.length === 0 && <div className="threads-empty">No conversations yet</div>}
      </nav>

      <div className="sidebar-foot">
        <div className="user">{authEnabled ? account?.username ?? "Signed in" : "Local dev mode"}</div>
        {authEnabled && (
          <button className="logout" onClick={logout}>
            Sign out
          </button>
        )}
      </div>
    </aside>
  );
}
