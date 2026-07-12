import { useState } from 'react';
import type { GameSummary, Machine, Command, Conflict } from '../types';
import { GamesSidebar } from './GamesSidebar';
import { GameDetail } from './GameDetail';

interface Props {
  games: GameSummary[];
  machines: Machine[];
  commands: Command[];
  conflicts: Conflict[];
  onRefresh: () => void;
  onAddGame: () => void;
}

export function GamesView({ games, machines, commands, conflicts, onRefresh, onAddGame }: Props) {
  const [selectedId, setSelectedId] = useState<string | null>(
    games.length > 0 ? games[0].game.id : null
  );

  // Keep selectedId in sync when games list changes (e.g., a game is deleted).
  const validIds = new Set(games.map(s => s.game.id));
  const activeId = selectedId && validIds.has(selectedId) ? selectedId : (games[0]?.game.id ?? null);

  const selectedSummary = games.find(s => s.game.id === activeId) ?? null;

  if (games.length === 0) {
    return (
      <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#556070', fontSize: 14 }}>
        No games tracked yet. Click "+ Add game" to define one.
      </div>
    );
  }

  return (
    <div style={{ flex: 1, display: 'flex', minHeight: 0, overflow: 'hidden' }}>
      <GamesSidebar games={games} selectedId={activeId} onSelect={id => setSelectedId(id)} onAddGame={onAddGame} onRefresh={onRefresh} />

      <main style={{ flex: 1, overflowY: 'auto', padding: '20px 24px' }}>
        {selectedSummary ? (
          <GameDetail
            key={selectedSummary.game.id}
            summary={selectedSummary}
            machines={machines}
            commands={commands}
            conflicts={conflicts}
            onRefresh={onRefresh}
          />
        ) : (
          <div style={{ color: '#556070', fontSize: 14, marginTop: 40, textAlign: 'center' }}>Select a game from the sidebar.</div>
        )}
      </main>
    </div>
  );
}
