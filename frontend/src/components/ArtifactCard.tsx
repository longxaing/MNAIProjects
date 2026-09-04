import { useEffect, useState } from "react";
import { downloadArtifact, getArtifactViewUrl } from "../api/client";
import type { Artifact } from "../api/types";

export default function ArtifactCard({
  artifact,
  threadId
}: {
  artifact: Artifact;
  threadId: string;
}) {
  const kb = Math.max(1, Math.round(artifact.sizeBytes / 1024));
  const [previewUrl, setPreviewUrl] = useState("");
  const [previewError, setPreviewError] = useState("");

  useEffect(() => {
    if (artifact.kind !== "uiScreenshot") return;
    let active = true;
    let objectUrl = "";
    void getArtifactViewUrl(threadId, artifact.id)
      .then(({ url, revoke }) => {
        if (!active) {
          if (revoke) URL.revokeObjectURL(url);
          return;
        }
        objectUrl = revoke ? url : "";
        setPreviewUrl(url);
      })
      .catch((reason: unknown) => {
        if (active) {
          setPreviewError(reason instanceof Error ? reason.message : "Unable to load screenshot.");
        }
      });
    return () => {
      active = false;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [artifact.id, artifact.kind, threadId]);

  if (artifact.kind === "uiScreenshot") {
    return (
      <figure className="ui-screenshot">
        {previewUrl ? (
          <img src={previewUrl} alt={artifact.fileName} />
        ) : (
          <div className="ui-screenshot-loading">{previewError || "Loading UI preview..."}</div>
        )}
        <figcaption>
          <span>
            <strong>{artifact.fileName}</strong>
            <small>{kb} KB</small>
          </span>
          <button
            className="artifact-dl"
            onClick={() => void downloadArtifact(threadId, artifact.id, artifact.fileName)}
          >
            Download
          </button>
        </figcaption>
      </figure>
    );
  }

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
