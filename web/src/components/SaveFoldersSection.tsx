import { useEffect, useState } from 'react';
import { api } from '../api';
import type { MachineSavePath, SavePath } from '../types';

const card = { background: '#1E252A', border: '1px solid #494949', borderRadius: 8, overflow: 'hidden' } as const;
const sectionLabel = { fontSize: 10, fontWeight: 700, color: '#129271', letterSpacing: '0.12em', textTransform: 'uppercase' as const };
const mono = { fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", fontSize: 11 } as const;
const input = {
  background: '#2A3238', border: '1px solid #494949', borderRadius: 5, color: '#ECEFF1',
  padding: '5px 8px', fontSize: 12, fontFamily: 'inherit',
} as const;
function button(kind: 'plain' | 'danger' | 'primary', disabled: boolean) {
  const color = kind === 'danger' ? '#e05252' : kind === 'primary' ? '#129271' : '#ECEFF1';
  return {
    padding: '4px 10px', borderRadius: 5, fontSize: 11, fontWeight: 600, fontFamily: 'inherit',
    background: kind === 'primary' ? '#129271' : 'transparent', color: kind === 'primary' ? '#fff' : color,
    border: `1px solid ${kind === 'plain' ? '#494949' : color}`, cursor: disabled ? 'default' : 'pointer',
    opacity: disabled ? 0.5 : 1,
  } as const;
}

interface Props {
  gameId: string;
  extraPaths: SavePath[];
  onRefresh: () => void;
}

/**
 * A game's extra save folders (beyond the main one): each with its template, include scope and where every
 * machine has it. Adding one defines it for every machine — each finds its own from the template, or maps it
 * by hand (`savelocker add-path`). Removing one stops syncing it everywhere; the files stay where they are.
 */
export function SaveFoldersSection({ gameId, extraPaths, onRefresh }: Props) {
  const [open, setOpen] = useState(extraPaths.length > 0);
  const [mapped, setMapped] = useState<MachineSavePath[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [draft, setDraft] = useState({ key: '', template: '', include: '' });

  useEffect(() => {
    api.getGamePaths(gameId).then(setMapped).catch(() => setMapped([]));
  }, [gameId, extraPaths.length]);

  async function run(work: () => Promise<unknown>) {
    setBusy(true);
    setError('');
    try { await work(); onRefresh(); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }

  const add = () => run(async () => {
    const include = draft.include.split(',').map(s => s.trim()).filter(Boolean);
    await api.addSavePath(gameId, {
      key: draft.key.trim(), label: null,
      template: draft.template.trim() || null, includeGlobs: include.length ? include : null,
    });
    setDraft({ key: '', template: '', include: '' });
  });

  const remove = (key: string) => {
    if (!confirm(`Stop syncing the '${key}' folder on every machine? Its files stay where they are.`)) return;
    void run(() => api.removeSavePath(gameId, key));
  };

  return (
    <div style={card}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{ width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '11px 18px', background: 'transparent', border: 'none', cursor: 'pointer', textAlign: 'left' }}
      >
        <span style={sectionLabel}>Extra save folders ({extraPaths.length})</span>
        <span style={{ fontSize: 11, color: '#556070', userSelect: 'none' }}>{open ? '▲' : '▼'}</span>
      </button>
      {open && (
        <div style={{ padding: '10px 18px 14px', display: 'flex', flexDirection: 'column', gap: 10, borderTop: '1px solid #494949' }}>
          {extraPaths.length === 0 && (
            <div style={{ color: '#9CA3AF', fontSize: 12 }}>
              Only the main save folder is synced. Add one when a game keeps saves in more than one place.
            </div>
          )}
          {extraPaths.map(p => {
            const machines = mapped.filter(m => m.pathKey === p.key);
            return (
              <div key={p.key} style={{ borderTop: '1px solid #252e35', paddingTop: 8 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <span style={{ color: '#ECEFF1', fontSize: 13, fontWeight: 600 }}>{p.label || p.key}</span>
                  {p.label && <span style={{ ...mono, color: '#9CA3AF' }}>{p.key}</span>}
                  <button style={{ ...button('danger', busy), marginLeft: 'auto' }} disabled={busy} onClick={() => remove(p.key)}>Remove</button>
                </div>
                <div style={{ ...mono, color: '#9CA3AF', marginTop: 3 }}>
                  {p.template ? `Template: ${p.template}` : 'No template — each machine maps it by hand'}
                </div>
                {p.includeGlobs && p.includeGlobs.length > 0 && (
                  <div style={{ ...mono, color: '#9CA3AF' }}>Only: {p.includeGlobs.join(', ')}</div>
                )}
                {machines.length === 0
                  ? <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 3 }}>Not mapped on any machine yet; each syncs it through a hidden copy until it is.</div>
                  : machines.map(m => (
                    <div key={m.machineId} style={{ fontSize: 11, marginTop: 3, color: '#ECEFF1' }}>
                      {m.machineName}: <span style={{ ...mono, color: '#9CA3AF' }}>{m.savePath}</span>
                    </div>
                  ))}
              </div>
            );
          })}
          <div style={{ borderTop: '1px solid #252e35', paddingTop: 10, display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' }}>
            <input style={{ ...input, width: 110 }} placeholder="key (e.g. config)" value={draft.key}
              onChange={e => setDraft({ ...draft, key: e.target.value })} />
            <input style={{ ...input, flex: 1, minWidth: 220 }} placeholder="template (e.g. <winDocuments>/My Games/Game/Config)"
              value={draft.template} onChange={e => setDraft({ ...draft, template: e.target.value })} />
            <input style={{ ...input, width: 140 }} placeholder="only (e.g. *.ini)" value={draft.include}
              onChange={e => setDraft({ ...draft, include: e.target.value })} />
            <button style={button('primary', busy || !draft.key.trim())} disabled={busy || !draft.key.trim()} onClick={() => void add()}>
              Add folder
            </button>
          </div>
          {error && <div style={{ color: '#e05252', fontSize: 12 }}>{error}</div>}
        </div>
      )}
    </div>
  );
}
