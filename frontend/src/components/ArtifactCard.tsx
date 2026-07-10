import { downloadArtifact } from "../api/client";
import type { Artifact } from "../api/types";

export default function ArtifactCard({ artifact }: { artifact: Artifact }) {
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
        onClick={() => void downloadArtifact(artifact.downloadUrl, artifact.fileName)}
      >
        Download
      </button>
    </div>
  );
}
