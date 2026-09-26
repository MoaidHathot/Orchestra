import React, { useEffect, useMemo, useState } from 'react';
import { api } from '../../api';
import { Icons } from '../../icons';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import { getRespondedBy, setRespondedBy } from '../../identity';
import type { PendingInputRecord, HumanInputResponse } from '../../types';

interface Props {
	open: boolean;
	onClose: () => void;
	/** Live list of pending input records (owned by App.tsx via usePendingInputs). */
	records: PendingInputRecord[];
	/** Whether the initial GET /api/runs/pending is still in flight. */
	loading: boolean;
	/** Called by the modal after a successful POST /respond so the parent can drop
	 *  the record locally without waiting for the SSE round-trip. */
	onResponded: (orchestrationName: string, runId: string, stepName: string) => void;
}

interface SubmitState {
	submitting: boolean;
	/** Inline error message shown next to the submit button (validation, 404, 5xx, ...). */
	error: string | null;
}

/**
 * Returns a short relative-age string like "just now", "3m", "2h", "1d 4h" given
 * a timestamp in the past. Cheap to compute on every render — we don't tick.
 */
function formatAge(iso: string): string {
	const then = new Date(iso).getTime();
	if (Number.isNaN(then)) return '';
	const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
	if (seconds < 30) return 'just now';
	if (seconds < 60) return `${seconds}s`;
	if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
	if (seconds < 86_400) {
		const h = Math.floor(seconds / 3600);
		const m = Math.floor((seconds % 3600) / 60);
		return m > 0 ? `${h}h ${m}m` : `${h}h`;
	}
	const d = Math.floor(seconds / 86_400);
	const h = Math.floor((seconds % 86_400) / 3600);
	return h > 0 ? `${d}d ${h}h` : `${d}d`;
}

/** Returns true when expiresAt is within 5 minutes from now (or already past). */
function isExpiringSoon(iso: string | undefined): boolean {
	if (!iso) return false;
	const ms = new Date(iso).getTime();
	if (Number.isNaN(ms)) return false;
	return ms - Date.now() < 5 * 60 * 1000;
}

function recordKey(r: Pick<PendingInputRecord, 'orchestrationName' | 'runId' | 'stepName'>): string {
	return `${r.orchestrationName}|${r.runId}|${r.stepName}`;
}

export default function WaitingInputsModal({
	open,
	onClose,
	records,
	loading,
	onResponded,
}: Props): React.JSX.Element {
	// No Escape/backdrop dismissal — holds unsaved human-input replies. Explicit close only.
	const trapRef = useFocusTrap<HTMLDivElement>(open);

	// Selected record key (composite). When null, defaults to the first record.
	const [selectedKey, setSelectedKey] = useState<string | null>(null);

	// Form state
	const [choice, setChoice] = useState<string | null>(null);
	const [reply, setReply] = useState('');
	const [respondedBy, setRespondedByLocal] = useState(() => getRespondedBy() ?? '');
	const [submit, setSubmit] = useState<SubmitState>({ submitting: false, error: null });

	// Pick a default selection when the list changes.
	const selected = useMemo(() => {
		if (records.length === 0) return null;
		if (selectedKey) {
			const found = records.find(r => recordKey(r) === selectedKey);
			if (found) return found;
		}
		return records[0];
	}, [records, selectedKey]);

	// When the selected record changes, reset form state to its defaults.
	useEffect(() => {
		if (!selected) {
			setChoice(null);
			setReply('');
			setSubmit({ submitting: false, error: null });
			return;
		}
		setChoice(selected.choices && selected.choices.length > 0 ? selected.choices[0] : null);
		setReply('');
		setSubmit({ submitting: false, error: null });
	}, [selected ? recordKey(selected) : null]);

	const handleSubmit = async () => {
		if (!selected) return;

		const trimmedReply = reply.trim();
		if (!choice && !trimmedReply) {
			setSubmit({
				submitting: false,
				error: 'Please pick a choice or write a reply before submitting.',
			});
			return;
		}

		// Persist the user's display name on first submit so we don't re-prompt.
		setRespondedBy(respondedBy);

		const body: HumanInputResponse = {
			choice: choice ?? null,
			reply: trimmedReply.length > 0 ? trimmedReply : null,
			respondedBy: respondedBy.trim().length > 0 ? respondedBy.trim() : null,
		};

		setSubmit({ submitting: true, error: null });
		try {
			const url = `/api/orchestrations/${encodeURIComponent(selected.orchestrationName)}/runs/${encodeURIComponent(selected.runId)}/respond?step=${encodeURIComponent(selected.stepName)}`;
			await api.post(url, body);
			onResponded(selected.orchestrationName, selected.runId, selected.stepName);
			// Selection will reset because `records` changes via the parent's removal.
		} catch (err) {
			const message = err instanceof Error ? err.message : String(err);
			// 404 is a known race: the wait was satisfied or cancelled between the SSE
			// event and our POST. Drop the record locally so the user can move on.
			if (/no pending input|404/i.test(message)) {
				onResponded(selected.orchestrationName, selected.runId, selected.stepName);
				setSubmit({
					submitting: false,
					error: 'This wait was already resolved on the server. Removed from the list.',
				});
				return;
			}
			setSubmit({ submitting: false, error: message });
		}
	};

	return (
		<div
			className={`modal-overlay ${open ? 'visible' : ''}`}
			ref={trapRef}
		>
			<div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="Orchestrations waiting for input">
				<div className="modal-header">
					<div className="modal-title">
						<Icons.Hand /> Waiting for Input ({records.length})
					</div>
					<button className="modal-close" aria-label="Close" onClick={onClose}>
						<Icons.X />
					</button>
				</div>
				<div className="modal-body">
					{loading ? (
						<div className="empty-state">
							<div className="spinner"></div>
						</div>
					) : records.length === 0 ? (
						<div className="empty-state">
							<div className="empty-title">All caught up</div>
							<div className="empty-text">
								No orchestrations are waiting for input. They&apos;ll show up here in real time when they pause.
							</div>
						</div>
					) : (
						<div className="waiting-inputs-grid">
							<div className="waiting-inputs-list" role="listbox" aria-label="Pending waits">
								{records.map(r => {
									const key = recordKey(r);
									const isSelected = selected && recordKey(selected) === key;
									return (
										<button
											key={key}
											type="button"
											role="option"
											aria-selected={isSelected ? 'true' : 'false'}
											className={`waiting-inputs-row ${isSelected ? 'selected' : ''}`}
											onClick={() => setSelectedKey(key)}
										>
											<div className="waiting-inputs-row-top">
												<span className="waiting-inputs-orch">{r.orchestrationName}</span>
												<span className={`waiting-inputs-kind kind-${r.kind.toLowerCase()}`}>{r.kind}</span>
											</div>
											<div className="waiting-inputs-row-mid">
												<span className="waiting-inputs-step">{r.stepName}</span>
											</div>
											<div className="waiting-inputs-row-bot">
												<span className="text-muted" style={{ fontSize: '11px' }}>
													waiting {formatAge(r.createdAt)}
												</span>
												{r.expiresAt && (
													<span
														className={`waiting-inputs-expiry ${isExpiringSoon(r.expiresAt) ? 'soon' : ''}`}
														title={`Expires ${r.expiresAt}`}
													>
														expires in {formatAge(r.expiresAt).replace('h', 'h').replace('m', 'm') /* keep same format */}
													</span>
												)}
											</div>
										</button>
									);
								})}
							</div>
							<div className="waiting-inputs-detail">
								{selected && (
									<>
										<div className="waiting-inputs-detail-header">
											<div>
												<div className="waiting-inputs-detail-orch">{selected.orchestrationName}</div>
												<div className="text-muted" style={{ fontSize: '12px' }}>
													run <code>{selected.runId}</code> · step <strong>{selected.stepName}</strong>
												</div>
											</div>
											<span className={`waiting-inputs-kind kind-${selected.kind.toLowerCase()}`}>{selected.kind}</span>
										</div>

										<div className="waiting-inputs-prompt">
											<div className="text-muted" style={{ fontSize: '11px', marginBottom: '4px' }}>Prompt</div>
											<pre className="waiting-inputs-prompt-text">{selected.prompt}</pre>
										</div>

										{selected.choices && selected.choices.length > 0 && (
											<div className="waiting-inputs-choices">
												<div className="text-muted" style={{ fontSize: '11px', marginBottom: '4px' }}>Choice</div>
												<div role="radiogroup" aria-label="Available choices">
													{selected.choices.map(c => (
														<label key={c} className="waiting-inputs-choice">
															<input
																type="radio"
																name={`choice-${recordKey(selected)}`}
																value={c}
																checked={choice === c}
																onChange={() => setChoice(c)}
															/>
															<span>{c}</span>
														</label>
													))}
												</div>
											</div>
										)}

										<div className="waiting-inputs-reply">
											<div className="text-muted" style={{ fontSize: '11px', marginBottom: '4px' }}>
												{selected.choices && selected.choices.length > 0 ? 'Reply (optional)' : 'Reply'}
											</div>
											<textarea
												value={reply}
												onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setReply(e.target.value)}
												placeholder={
													selected.choices && selected.choices.length > 0
														? 'Add a comment alongside your choice...'
														: 'Type your response...'
												}
												rows={5}
											/>
										</div>

										<div className="waiting-inputs-identity">
											<div className="text-muted" style={{ fontSize: '11px', marginBottom: '4px' }}>
												Your name (optional, for the audit trail)
											</div>
											<input
												type="text"
												value={respondedBy}
												onChange={(e: React.ChangeEvent<HTMLInputElement>) => setRespondedByLocal(e.target.value)}
												placeholder="alice"
											/>
										</div>

										{submit.error && (
											<div className="waiting-inputs-error" role="alert">
												{submit.error}
											</div>
										)}

										<div className="waiting-inputs-actions">
											<button
												className="btn btn-primary"
												disabled={submit.submitting}
												onClick={handleSubmit}
											>
												{submit.submitting ? 'Submitting...' : 'Submit response'}
											</button>
										</div>
									</>
								)}
							</div>
						</div>
					)}
				</div>
				<div className="modal-footer">
					<button className="btn" onClick={onClose}>
						Close
					</button>
				</div>
			</div>
		</div>
	);
}
