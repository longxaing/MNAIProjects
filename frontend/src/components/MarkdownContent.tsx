import { memo, useEffect, useId, useMemo, useState } from "react";
import Markdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

function MermaidDiagram({ source }: { source: string }) {
  const rawId = useId();
  const diagramId = `mermaid-${rawId.replace(/[^a-zA-Z0-9_-]/g, "")}`;
  const normalizedSource = source.replace(/\\n/g, "<br/>");
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
        const parsed = await mermaid.parse(normalizedSource, { suppressErrors: true });
        if (!parsed) throw new Error("Invalid Mermaid syntax.");

        const renderContainer = document.createElement("div");
        renderContainer.style.cssText =
          "position:fixed;left:-100000px;top:0;opacity:0;pointer-events:none;";
        document.body.appendChild(renderContainer);
        try {
          const result = await mermaid.render(diagramId, normalizedSource, renderContainer);
          if (active) {
            setSvg(result.svg);
            setError("");
          }
        } finally {
          renderContainer.remove();
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
  }, [diagramId, normalizedSource]);

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

function MarkdownContent({
  content,
  renderMermaid = true
}: {
  content: string;
  renderMermaid?: boolean;
}) {
  const components = useMemo<Components>(
    () => ({
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
    }),
    [renderMermaid]
  );

  return (
    <Markdown remarkPlugins={[remarkGfm]} components={components}>
      {content}
    </Markdown>
  );
}

export default memo(MarkdownContent);