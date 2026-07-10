import { downloadArtifact } from "../api/client";
import type { Artifact } from "../api/types";

export default function ArtifactCard({
  artifact,
  threadId
}: {
  artifact: Artifact;
  threadId: string;
}) {
  const kb = Math.max(1, Math.round(artifact.sizeBytes / 1024));
  return (
    <div className="artifact">
      <div className={`artifact-badge ${artifact.kind}`}>{artifact.kind.toUpperCase()}</div>
      <div className="artifact-meta">
        <div className="artifact-name" title={artifact.fileName}>
          {artifact.fileName}
        </div>
        <div className="artifact-sub">{kb} KB</div>
      </div>
      <button
        className="artifact-dl"
        onClick={() => void downloadArtifact(threadId, artifact.id, artifact.fileName)}
      >
        Download
      </button>
    </div>
  );
}
