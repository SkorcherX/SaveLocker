import installingTheAgent from './installing-the-agent.md?raw';
import conflicts from './conflicts.md?raw';
import howSyncWorks from './how-sync-works.md?raw';
import multiMachine from './multi-machine.md?raw';
import deckSupportedGames from './deck-supported-games.md?raw';
import saveInUseSafety from './save-in-use-safety.md?raw';
import globPatterns from './glob-patterns.md?raw';
import addingGames from './adding-games.md?raw';
import emulatorBackups from './emulator-backups.md?raw';
import saveRetention from './save-retention.md?raw';
import agentUpdate from './agent-update.md?raw';
import cliReference from './cli-reference.md?raw';
import troubleshooting from './troubleshooting.md?raw';
import restoreSafety from './restore-safety.md?raw';
import steamLibraryArtwork from './steam-library-artwork.md?raw';
import deckyPlugin from './decky-plugin.md?raw';
import databaseBackups from './database-backups.md?raw';
import emulatorSaves from './emulator-saves.md?raw';
import multipleSaveFolders from './multiple-save-folders.md?raw';
import saveFolderEmptied from './save-folder-emptied.md?raw';

export interface Article {
  slug: string;
  title: string;
  category: string;
  content: string;
}

export const articles: Article[] = [
  // First, deliberately: `categories` preserves this order, so setup leads the sidebar.
  { slug: 'installing-the-agent', title: 'Installing the agent (Windows, Linux & Steam Deck)', category: 'Getting started', content: installingTheAgent },
  { slug: 'conflicts',       title: 'Understanding sync conflicts',           category: 'Syncing',        content: conflicts },
  { slug: 'how-sync-works',  title: 'How syncing works',                      category: 'Syncing',        content: howSyncWorks },
  { slug: 'multi-machine',   title: 'Best practices for multiple machines',   category: 'Syncing',        content: multiMachine },
  { slug: 'deck-supported-games', title: 'What syncs on Steam Deck (and what doesn\'t)', category: 'Syncing', content: deckSupportedGames },
  { slug: 'save-in-use-safety', title: 'Save-in-use safety (the settle gate)', category: 'Syncing',      content: saveInUseSafety },
  { slug: 'emulator-backups', title: 'Emulator backup best practices',       category: 'Syncing',        content: emulatorBackups },
  { slug: 'glob-patterns',   title: 'Exclude patterns (glob filters)',         category: 'Configuration',  content: globPatterns },
  { slug: 'adding-games',    title: 'Adding games & mapping save folders',     category: 'Configuration',  content: addingGames },
  { slug: 'multiple-save-folders', title: 'Games with more than one save folder', category: 'Configuration', content: multipleSaveFolders },
  { slug: 'emulator-saves',  title: 'Setting up emulator saves',              category: 'Configuration',  content: emulatorSaves },
  { slug: 'steam-library-artwork', title: 'Steam Library artwork (Steam Deck)', category: 'Configuration', content: steamLibraryArtwork },
  { slug: 'decky-plugin',    title: 'The Decky plugin (Steam Deck)',           category: 'Configuration',  content: deckyPlugin },
  { slug: 'save-retention',  title: 'Save retention',                         category: 'Configuration',  content: saveRetention },
  { slug: 'database-backups', title: 'Backups and restoring',                category: 'Maintenance',    content: databaseBackups },
  { slug: 'agent-update',    title: 'Agent auto-update & fetching from GitHub','category': 'Maintenance', content: agentUpdate },
  { slug: 'cli-reference',   title: 'Agent CLI reference',                    category: 'Reference',      content: cliReference },
  { slug: 'troubleshooting', title: 'Troubleshooting',                        category: 'Troubleshooting',content: troubleshooting },
  { slug: 'save-folder-emptied', title: 'When every save file disappears',     category: 'Troubleshooting',content: saveFolderEmptied },
  { slug: 'restore-safety',  title: 'When a save is refused on restore',      category: 'Troubleshooting',content: restoreSafety },
];

export const categories = [...new Set(articles.map(a => a.category))];
