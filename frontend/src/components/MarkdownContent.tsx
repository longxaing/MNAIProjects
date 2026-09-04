import { useEffect, useId, useState } from "react";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

function MermaidDiagram({ source }: { source: string }) {
  const rawId = useId();
  const diagramId = `mermaid-${rawId.replace(/[^a-zA-Z0-9_-]/g, "")}`;
  const [svg, setSvg] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    let active = true;
    void import("mermaid").then(async ({ default: mermaid }) => {
      mermaid.initialize({
        startOnLoad: false,
        securityLevel: "strict",
        theme: "neutral"
      });
      try {
        const result = await mermaid.render(diagramId, source);
        if (active) {
          setSvg(result.svg);
          setError("");
        }
      } catch (reason) {
        if (active) {
          setSvg("");
          setError(reason instanceof Error ? reason.message : "Unable to render architecture diagram.");
        }
      }
    });
    return () => {
      active = false;
    };
  }, [diagramId, source]);

  if (error) {
    return <pre className="mermaid-error">{source}</pre>;
  }
  return (
    <div
      className="mermaid-diagram"
      aria-label="Architecture diagram"
      dangerouslySetInnerHTML={{ __html: svg }}
    />
  );
}

export default function MarkdownContent({
  content,
  renderMermaid = true
}: {
  content: string;
  renderMermaid?: boolean;
}) {
  const components: Components = {
    code({ className, children, ...props }) {
      const language = /language-([^\s]+)/.exec(className ?? "")?.[1];
      if (language === "mermaid" && renderMermaid) {
        return <MermaidDiagram source={String(children).trim()} />;
      }
      return (
        <code className={className} {...props}>
          {children}
        </code>
      );
    }
  };

  return (
    <Markdown remarkPlugins={[remarkGfm]} components={components}>
      {content}
    </Markdown>
  );
}