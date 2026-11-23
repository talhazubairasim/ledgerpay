import { useEffect, useState } from 'react';
import { api, money, type TransferRow } from '../api';
import { Alert, Badge, Card } from '../components/ui';

export function Transfers() {
  const [rows, setRows] = useState<TransferRow[]>([]);
  const [error, setError] = useState('');
  useEffect(() => { api.transfers().then(setRows).catch((e) => setError(e.message)); }, []);
  return (
    <div className="space-y-3">
      <h1 className="text-2xl font-bold">Transfers</h1>
      {error && <Alert>{error}</Alert>}
      <Card><TransferTable rows={rows} /></Card>
    </div>
  );
}

export function TransferTable({ rows, actions }: { rows: TransferRow[]; actions?: (t: TransferRow) => React.ReactNode }) {
  if (rows.length === 0) return <p className="text-sm text-slate-500">Nothing here.</p>;
  return (
    <table className="w-full text-sm">
      <thead><tr className="text-left text-slate-500"><th className="py-1">When</th><th>From</th><th>To</th><th>Note</th><th className="text-right">Amount</th><th>Status</th>{actions && <th />}</tr></thead>
      <tbody>
        {rows.map((t) => (
          <tr key={t.id} className="border-t align-top">
            <td className="py-2">{new Date(t.requestedAt).toLocaleString()}</td>
            <td className="font-mono text-xs">{t.fromNumber}</td>
            <td className="font-mono text-xs">{t.toNumber}</td>
            <td>{t.memo}{t.reasons.length > 0 && <div className="text-xs text-amber-700">{t.reasons.join(' · ')} (score {t.score})</div>}
              {t.rejectionReason && <div className="text-xs text-red-700">{t.rejectionReason}</div>}</td>
            <td className="text-right font-medium">{money(t.amount, t.currency)}</td>
            <td><Badge status={t.status} /></td>
            {actions && <td className="space-x-2 text-right">{actions(t)}</td>}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
