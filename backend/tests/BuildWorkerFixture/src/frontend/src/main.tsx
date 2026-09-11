import React, { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { getApiBaseUrl } from "./runtime-config";
import { title } from "./title";

function App() {
	const [note, setNote] = useState("");
	const [notes, setNotes] = useState<string[]>([]);
	const [error, setError] = useState("");
	async function loadNotes() {
		const response = await fetch(`${getApiBaseUrl()}/api/fixture-notes`);
		if (!response.ok) throw new Error(`Read failed: ${response.status}`);
		setNotes(await response.json());
	}
	useEffect(() => { loadNotes().catch(error => setError(String(error))); }, []);
	async function saveNote(event: React.FormEvent) {
		event.preventDefault();
		try {
			const response = await fetch(`${getApiBaseUrl()}/api/fixture-notes`, {
				method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(note)
			});
			if (!response.ok) throw new Error(`Save failed: ${response.status}`);
			setNote("");
			await loadNotes();
		} catch (error) { setError(String(error)); }
	}
	return (
	<main>
		<h1>{title}</h1>
		<p data-api-base-url={getApiBaseUrl()} />
		<form onSubmit={saveNote}>
			<label htmlFor="note">Note</label>
			<input id="note" value={note} onChange={event => setNote(event.target.value)} required />
			<button type="submit">Save note</button>
		</form>
		{error && <p role="alert">{error}</p>}
		<ul>{notes.map(value => <li key={value}>{value}</li>)}</ul>
	</main>
	);
}

createRoot(document.getElementById("root")!).render(<App />);