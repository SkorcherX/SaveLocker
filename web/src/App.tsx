import { useEffect, useRef, useState, useCallback } from 'react';
import { api, getPassword } from './api';
import type { GameSummary, Machine, Command, Conflict, Settings } from './types';
import { NavBar } from './components/NavBar';
import { GamesView } from './components/GamesView';
import { ConfigView } from './components/ConfigView';
import { AuditView } from './components/AuditView';

type View = 'games' | 'config' | 'audit';

interface AppData {
  games: GameSummary[];
  machines: Machine[];
  commands: Command[];
  conflicts: Conflict[];
  settings: Settings;
}

function getInitialView(): View {
  if (location.hash === '#config') return 'config';
  if (location.hash === '#audit') return 'audit';
  return 'games';
}

export default function App() {
  const [view, setView] = useState<View>(getInitialView);
  const [data, setData] = useState<AppData | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const loadingRef = useRef(false);

  const load = useCallback(async () => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    setLoading(true);
    setError('');
    try {
      const [games, conflicts, machines, commands, settings] = await Promise.all([
        api.overview(), api.conflicts(), api.machines(), api.commands(), api.settings(),
      ]);
      setData({ games, machines, commands, conflicts, settings });
    } catch (e) {
      const msg = (e as Error).message;
      setError(msg.startsWith('401') ? 'Wrong password — enter it in the nav bar and click Connect.' : 'Failed to load: ' + msg);
    } finally {
      setLoading(false);
      loadingRef.current = false;
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    const id = setInterval(load, 15000);
    return () => clearInterval(id);
  }, [load]);

  useEffect(() => {
    location.hash = view === 'config' ? 'config' : view === 'audit' ? 'audit' : '';
  }, [view]);

  async function handleAddGame() {
    const name = prompt('New game name (defines it on the server; agents map their local save dir):');
    if (!name?.trim()) return;
    const dir = prompt(
      'Suggested save folder (optional). E.g. C:\\Users\\me\\AppData\\Roaming\\Game. Leave blank to skip:',
      ''
    );
    try {
      await api.addGame(name.trim(), dir?.trim() || null);
      await load();
    } catch (e) { alert('Add game failed: ' + (e as Error).message); }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
      <NavBar
        view={view}
        onViewChange={v => { setView(v); if (!data) load(); }}
        onConnect={load}
        onAddGame={handleAddGame}
        onRefresh={load}
        isGamesView={view === 'games'}
      />

      {error && (
        <div style={{ padding: '10px 24px', color: '#f4a60d', fontSize: 13 }}>{error}</div>
      )}

      {!getPassword() && !data && !loading && !error && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#556070', fontSize: 14 }}>
          If a password is required, enter it in the nav bar and click Connect.
        </div>
      )}

      {loading && !data && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#556070', fontSize: 13 }}>
          Loading…
        </div>
      )}

      {data && (
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          {view === 'games'
            ? <GamesView
                games={data.games}
                machines={data.machines}
                commands={data.commands}
                conflicts={data.conflicts}
                onRefresh={load}
              />
            : view === 'audit'
            ? <AuditView />
            : <ConfigView
                games={data.games}
                machines={data.machines}
                settings={data.settings}
                onRefresh={load}
              />
          }
        </div>
      )}
    </div>
  );
}
