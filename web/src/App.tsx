import { useEffect, useRef, useState, useCallback } from 'react';
import { api, getApiKey } from './api';
import type { GameSummary, Machine, Command, Conflict, Settings } from './types';
import { NavBar } from './components/NavBar';
import { GamesView } from './components/GamesView';
import { ConfigView } from './components/ConfigView';

type View = 'games' | 'config';

interface AppData {
  games: GameSummary[];
  machines: Machine[];
  commands: Command[];
  conflicts: Conflict[];
  settings: Settings;
}

function getInitialView(): View {
  return location.hash === '#config' ? 'config' : 'games';
}

export default function App() {
  const [view, setView] = useState<View>(getInitialView);
  const [data, setData] = useState<AppData | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const loadingRef = useRef(false);

  const load = useCallback(async () => {
    if (!getApiKey()) {
      setData(null);
      return;
    }
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
      setError('Failed to load: ' + (e as Error).message + ' (check the API key).');
    } finally {
      setLoading(false);
      loadingRef.current = false;
    }
  }, []);

  useEffect(() => { if (getApiKey()) load(); }, [load]);

  useEffect(() => {
    const id = setInterval(() => { if (getApiKey()) load(); }, 15000);
    return () => clearInterval(id);
  }, [load]);

  useEffect(() => {
    location.hash = view === 'config' ? 'config' : '';
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

      {!getApiKey() && !data && (
        <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#556070', fontSize: 14 }}>
          Enter an API key and click Connect.
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
            : <ConfigView
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
