import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';
import type { BackupInfo, BackupStatus } from '../types';

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

const REASON_LABELS: Record<string, string> = {
  Scheduled: 'Scheduled',
  Manual: 'Manual',
  BeforeUpgrade: 'Before upgrade',
  BeforeRestore: 'Before restore',
};

function formatBytes(n: number) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`;
  return `${(n / 1024 ** 3).toFixed(2)} GB`;
}

function formatUtc(iso: string | null | undefined) {
  if (!iso) return '—';
  const d = new Date(/[Z+]/.test(iso.slice(-6)) ? iso : iso + 'Z');
  return d.toLocaleString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

const card: React.CSSProperties = {
  background: '#1F262B', border: '1px solid #494949', borderRadius: 8, padding: 16, marginBottom: 16,
};
const label: React.CSSProperties = { color: '#9CA3AF', fontSize: 12 };
const input: React.CSSProperties = {
  background: '#2A3238', border: '1px solid #494949', borderRadius: 5, color: '#ECEFF1',
  padding: '5px 8px', fontSize: 13, fontFamily: 'inherit',
};
function button(kind: 'primary' | 'plain' | 'danger', disabled = false): React.CSSProperties {
  const color = kind === 'primary' ? '#129271' : kind === 'danger' ? '#e05252' : '#ECEFF1';
  return {
    padding: '5px 12px', borderRadius: 5, fontSize: 12, fontWeight: 600, fontFamily: 'inherit',
    background: kind === 'primary' ? '#129271' : 'transparent',
    color: kind === 'primary' ? '#fff' : color,
    border: `1px solid ${kind === 'plain' ? '#494949' : color}`,
    cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.5 : 1,
  };
}

/**
 * Backups of the server: the database plus every game's latest save, on a schedule or on demand, with
 * download, restore and delete. A restore replaces the live database — a safety backup of the current
 * state is taken first, so a restore can itself be undone from this list.
 */
export function BackupsView() {
  const [status, setStatus] = useState<BackupStatus | null>(null);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [draft, setDraft] = useState<{ enabled: boolean; frequency: string; dayOfWeek: number; hourOfDay: number; retentionCount: number } | null>(null);

  const load = useCallback(async () => {
    try {
      const s = await api.backupStatus();
      setStatus(s);
      setDraft(d => d ?? {
        enabled: s.enabled, frequency: s.frequency ?? 'weekly', dayOfWeek: Number(s.dayOfWeek ?? 0),
        hourOfDay: Number(s.hourOfDay), retentionCount: Number(s.retentionCount),
      });
      setError('');
    } catch (e) { setError((e as Error).message); }
  }, []);

  useEffect(() => { void load(); }, [load]);

  async function run(key: string, work: () => Promise<string | void>) {
    setBusy(key);
    setMessage('');
    setError('');
    try {
      const m = await work();
      if (m) setMessage(m);
      await load();
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(null); }
  }

  const backupNow = () => run('now', async () => {
    const r = await api.backupNow();
    return r.ok ? `Backed up to ${r.backup?.fileName}.` : `Backup failed: ${r.message}`;
  });

  const saveSchedule = () => draft && run('settings', async () => {
    await api.setBackupSettings(draft);
    return 'Schedule saved.';
  });

  const download = (b: BackupInfo) => run(`dl:${b.fileName}`, async () => {
    const t = await api.backupDownloadTicket(b.fileName);
    // A plain navigation, so the browser streams a multi-GB file to disk instead of buffering it.
    window.location.href = t.url;
  });

  const restore = (b: BackupInfo) => {
    if (!confirm(
      `Restore ${b.fileName}?\n\nThis REPLACES the server's database with the one in this backup: every game, ` +
      'version, machine and setting goes back to that moment. A safety backup of the current state is taken first.',
    )) return;
    void run(`restore:${b.fileName}`, async () => {
      const r = await api.restoreBackup(b.fileName);
      return `Restored ${r.restoredFrom}. ${r.savesRestored} save(s) put back; safety backup: ${r.safetyBackup}.` +
        (r.warning ? ` Warning: ${r.warning}` : '');
    });
  };

  const remove = (b: BackupInfo) => {
    if (!confirm(`Delete ${b.fileName}? This cannot be undone.`)) return;
    void run(`del:${b.fileName}`, async () => { await api.deleteBackup(b.fileName); return `Deleted ${b.fileName}.`; });
  };

  if (!status || !draft) {
    return <div style={{ padding: 24, color: error ? '#e05252' : '#9CA3AF' }}>{error || 'Loading backups…'}</div>;
  }

  return (
    <div style={{ padding: 24, maxWidth: 960, margin: '0 auto', overflowY: 'auto', height: '100%', boxSizing: 'border-box' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
        <h2 style={{ margin: 0, color: '#ECEFF1', fontSize: 18 }}>Backups</h2>
        <button style={{ ...button('primary', busy !== null), marginLeft: 'auto' }} disabled={busy !== null} onClick={backupNow}>
          {busy === 'now' ? 'Backing up…' : 'Back up now'}
        </button>
      </div>

      {error && <div style={{ ...card, borderColor: '#e05252', color: '#e05252' }}>{error}</div>}
      {message && <div style={{ ...card, color: '#ECEFF1' }}>{message}</div>}
      {status.lastError && (
        <div style={{ ...card, borderColor: '#f4a60d', color: '#f4a60d' }}>
          The last scheduled backup failed ({formatUtc(status.lastErrorAt)}): {status.lastError}
        </div>
      )}

      <div style={card}>
        <div style={{ color: '#ECEFF1', fontWeight: 600, marginBottom: 12 }}>Schedule</div>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 16, alignItems: 'center' }}>
          <label style={{ ...label, display: 'flex', alignItems: 'center', gap: 6 }}>
            <input type="checkbox" checked={draft.enabled} onChange={e => setDraft({ ...draft, enabled: e.target.checked })} />
            Scheduled backups
          </label>
          <label style={label}>
            Every{' '}
            <select style={input} value={draft.frequency} onChange={e => setDraft({ ...draft, frequency: e.target.value })}>
              <option value="daily">day</option>
              <option value="weekly">week</option>
            </select>
          </label>
          {draft.frequency === 'weekly' && (
            <label style={label}>
              on{' '}
              <select style={input} value={draft.dayOfWeek} onChange={e => setDraft({ ...draft, dayOfWeek: Number(e.target.value) })}>
                {DAYS.map((d, i) => <option key={d} value={i}>{d}</option>)}
              </select>
            </label>
          )}
          <label style={label}>
            at{' '}
            <select style={input} value={draft.hourOfDay} onChange={e => setDraft({ ...draft, hourOfDay: Number(e.target.value) })}>
              {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
            </select>{' '}UTC
          </label>
          <label style={label}>
            keep{' '}
            <input
              type="number" min={1} max={100} style={{ ...input, width: 60 }}
              value={draft.retentionCount}
              onChange={e => setDraft({ ...draft, retentionCount: Number(e.target.value) })}
            />{' '}backups
          </label>
          <button style={button('plain', busy !== null)} disabled={busy !== null} onClick={saveSchedule}>Save</button>
        </div>
        <div style={{ ...label, marginTop: 12 }}>
          Next run: {status.nextRunAt ? formatUtc(status.nextRunAt) : 'off'} · Stored in <code>{status.backupRoot}</code> ·
          {' '}Save archives on the server: {formatBytes(Number(status.archivesBytes))} in {status.archivesCount} version(s)
        </div>
      </div>

      <div style={card}>
        <div style={{ color: '#ECEFF1', fontWeight: 600, marginBottom: 12 }}>Backups ({status.backups.length})</div>
        {status.backups.length === 0 ? (
          <div style={label}>No backups yet.</div>
        ) : (
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
            <thead>
              <tr style={{ ...label, textAlign: 'left' }}>
                <th style={{ padding: '6px 4px' }}>Taken</th>
                <th style={{ padding: '6px 4px' }}>Why</th>
                <th style={{ padding: '6px 4px' }}>Contents</th>
                <th style={{ padding: '6px 4px' }}>Size</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {status.backups.map(b => (
                <tr key={b.fileName} style={{ borderTop: '1px solid #2A3238', color: '#ECEFF1' }}>
                  <td style={{ padding: '6px 4px' }} title={b.fileName}>{formatUtc(b.createdAt)}</td>
                  <td style={{ padding: '6px 4px' }}>{REASON_LABELS[String(b.reason)] ?? String(b.reason)}</td>
                  <td style={{ padding: '6px 4px', color: '#9CA3AF' }}>{b.includesSaves ? 'Database + latest saves' : 'Database only'}</td>
                  <td style={{ padding: '6px 4px', color: '#9CA3AF' }}>{formatBytes(Number(b.sizeBytes))}</td>
                  <td style={{ padding: '6px 4px', textAlign: 'right', whiteSpace: 'nowrap' }}>
                    <button style={button('plain', busy !== null)} disabled={busy !== null} onClick={() => void download(b)}>Download</button>{' '}
                    <button style={button('plain', busy !== null)} disabled={busy !== null} onClick={() => restore(b)}>
                      {busy === `restore:${b.fileName}` ? 'Restoring…' : 'Restore'}
                    </button>{' '}
                    <button style={button('danger', busy !== null)} disabled={busy !== null} onClick={() => remove(b)}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <div style={{ ...label, marginTop: 12 }}>
          A backup holds every machine key and password hash. The SteamGridDB key inside it is encrypted with a key
          ring kept in <code>keys/</code> beside the database, never in a backup — keep that folder with your data.
        </div>
      </div>
    </div>
  );
}
