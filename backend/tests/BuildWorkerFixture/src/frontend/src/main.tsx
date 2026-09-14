import React, { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { getApiBaseUrl } from "./runtime-config";
import { title } from "./title";
import "./styles.css";

function App() {
	const [note, setNote] = useState("");
	const [notes, setNotes] = useState<string[]>([]);
	const [error, setError] = useState("");
	const [saving, setSaving] = useState(false);
	const [loading, setLoading] = useState(true);
	async function loadNotes() {
		const response = await fetch(`${getApiBaseUrl()}/api/fixture-notes`);
		if (!response.ok) throw new Error(`Read failed: ${response.status}`);
		setNotes(await response.json());
	}
	useEffect(() => { loadNotes().catch(error => setError(String(error))).finally(() => setLoading(false)); }, []);
	async function saveNote(event: React.FormEvent) {
		event.preventDefault();
		if (saving) return;
		setSaving(true);
		setError("");
		try {
			const response = await fetch(`${getApiBaseUrl()}/api/fixture-notes`, {
				method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(note)
			});
			if (!response.ok) throw new Error(`Save failed: ${response.status}`);
			setNote("");
			await loadNotes();
		} catch (error) { setError(String(error)); }
		finally { setSaving(false); }
	}
	return (
	<main className="workspace" data-api-base-url={getApiBaseUrl()}>
		<header className="workspace-header">
			<span className="brand-mark" aria-hidden="true">N</span>
			<div><p className="eyebrow">PERSONAL WORKSPACE</p><h1>{title}</h1></div>
		</header>
		<div className="workspace-grid">
			<form className="composer" onSubmit={saveNote}>
				<h2>New note</h2>
				<div className="field">
					<label htmlFor="note">Note</label>
					<input id="note" value={note} onChange={event => setNote(event.target.value)} required />
				</div>
				<button className="primary" type="submit" disabled={saving}>{saving ? "Saving..." : "Save note"}</button>
				{error && <p className="error" role="alert">{error}</p>}
			</form>
			<section aria-labelledby="notes-heading" aria-busy={loading}>
				<div className="section-heading"><h2 id="notes-heading">Notes</h2><span className="count">{notes.length}</span></div>
				{loading ? <p className="empty" role="status">Loading...</p> : notes.length === 0 ? <p className="empty">No notes yet.</p> : null}
				<ul className="notes">{notes.map((value, index) => <li className="note" key={`${index}-${value}`}>{value}</li>)}</ul>
			</section>
		</div>
	</main>
	);
}

createRoot(document.getElementById("root")!).render(<App />);