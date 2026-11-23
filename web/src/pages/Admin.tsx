import { useEffect, useState } from 'react';
import { api, money, type StoredEvent, type TransferRow, type TrialBalance } from '../api';
import { Alert, Card, ghost } from '../components/ui';
import { TransferTable } from './Transfers';

export function Admin() {
  const [reviews, setReviews] = useState<TransferRow[]>([]);
  const [trial, setTrial] = useState<TrialBalance | null>(null);
  const [log, setLog] = useState<StoredEvent[]>([]);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    api.reviews().then(setReviews).catch((e) => setMessage({ ok: false, text: e.message }));
    api.trialBalance().then(setTrial).catch(() => {});
    api.log().then(setLog).catch(() => {});
  }, [tick]);

  const run = async (p: Promise<unknown>, text: string) => {
    try { await p; setMessage({ ok: true, text }); setTick((t) => t + 1); } catch (e) { setMessage({ ok: false, text: (e as Error).message }); }
  };

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Back office</h1>
      {message && <Alert kind={message.ok ? 'success' : 'error'}>{message.text}</Alert>}
      <Card title={`Review queue (${reviews.length})`}>
        <TransferTable rows={reviews} actions={(t) => <>
          <button className={ghost} onClick={() => run(api.approve(t.id), `Approved ${money(t.amount, t.currency)}`)}>Approve</button>
          <button className={ghost} onClick={() => run(api.reject(t.id, 'Rejected by reviewer'), 'Transfer rejected')}>Reject</button>
        </>} />
      </Card>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card title="Trial balance" action={<button className={ghost} onClick={() => run(api.rebuild(), 'Read models rebuilt from the event log')}>Rebuild projections</button>}>
          {trial && <>
            <p className={`mb-2 text-sm font-semibold ${trial.balanced ? 'text-emerald-700' : 'text-red-700'}`}>
              {trial.balanced ? `Balanced: every currency nets to zero and all ${trial.walletsChecked} wallets match the ledger.` : `Out of balance: ${trial.mismatches.join(', ')}`}
            </p>
            <table className="w-full text-sm">
              <thead><tr className="text-left text-slate-500"><th>Currency</th><th>Ledger</th><th className="text-right">Total</th></tr></thead>
              <tbody>{trial.lines.map((l) => (
                <tr key={`${l.currency}-${l.kind}`} className={`border-t ${l.kind === 'total' ? 'font-semibold' : ''}`}><td>{l.currency}</td><td>{l.kind}</td><td className="text-right">{money(l.total)}</td></tr>
              ))}</tbody>
            </table>
          </>}
        </Card>
        <Card title="Event log">
          <ol className="max-h-96 space-y-1 overflow-y-auto font-mono text-xs">
            {log.map((e) => <li key={e.position}><span className="text-slate-400">#{e.position}</span> <span className="text-indigo-700">{e.type}</span> <span className="text-slate-500">{e.streamId} v{e.version}</span></li>)}
          </ol>
        </Card>
      </div>
    </div>
  );
}
