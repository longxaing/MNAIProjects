import React from "react";
import { createRoot } from "react-dom/client";
import { getApiBaseUrl } from "./runtime-config";
import { title } from "./title";

createRoot(document.getElementById("root")!).render(
	<main>
		<h1>{title}</h1>
		<p data-api-base-url={getApiBaseUrl()} />
	</main>,
);