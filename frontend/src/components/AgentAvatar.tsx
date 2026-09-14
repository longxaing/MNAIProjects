export default function AgentAvatar({ className = "avatar bot" }: { className?: string }) {
  return (
    <span className={`${className} agent-avatar`} role="img" aria-label="AzurePilot">
      <img src={`${import.meta.env.BASE_URL}azurepilot-avatar.png`} alt="" width="48" height="48" />
    </span>
  );
}