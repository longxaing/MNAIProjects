import { useEffect, useId, useState } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";

function SharedDiagram({ source }: { source: string }) {
  const id = useId().replace(/[^a-zA-Z0-9_-]/g, "");
  const [image, setImage] = useState("");
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let active = true;
    setImage("");
    setFailed(false);
    if (source.length > 8000 || !/^\s*(flowchart|graph)\s+(LR|TD|TB|RL|BT)\b/.test(source)
      || /%%\{|^\s*---|\b(click|href|image|icon)\b|https?:|url\s*\(/im.test(source)) {
      setFailed(true);
      return;
    }
    void import("mermaid").then(async ({ default: mermaid }) => {
      mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: "neutral", maxTextSize: 8000, maxEdges: 150,
        htmlLabels: false, flowchart: { htmlLabels: false } });
      const container = document.createElement("div");
      container.style.cssText = "position:fixed;left:-20000px;visibility:hidden";
      document.body.append(container);
      try {
        const { svg } = await mermaid.render(`shared-diagram-${id}`, source, container);
        if (active) setImage(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`);
      } finally { container.remove(); }
    }).catch(() => { if (active) setFailed(true); });
    return () => { active = false; };
  }, [id, source]);
  if (failed) return <code>{source}</code>;
  return image ? <img className="shared-diagram" src={image} alt="Shared architecture diagram" /> : <span>Rendering diagram...</span>;
}

export default function SharedMarkdown({ content }: { content: string }) {
  return <Markdown remarkPlugins={[remarkGfm]} skipHtml urlTransform={() => ""} components={{
    a: ({ children }) => <span>{children}</span>,
    img: () => null,
    input: ({ checked }) => <span aria-label={checked ? "Checked" : "Unchecked"}>{checked ? "[x]" : "[ ]"}</span>,
    code: ({ className, children }) => className === "language-mermaid"
      ? <SharedDiagram source={String(children).trim()} /> : <code className={className}>{children}</code>
  }}>{content}</Markdown>;
}