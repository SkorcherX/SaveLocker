import conflicts from './conflicts.md?raw';
import howSyncWorks from './how-sync-works.md?raw';
import multiMachine from './multi-machine.md?raw';
import globPatterns from './glob-patterns.md?raw';
import addingGames from './adding-games.md?raw';
import saveRetention from './save-retention.md?raw';
import agentUpdate from './agent-update.md?raw';
import troubleshooting from './troubleshooting.md?raw';

export interface Article {
  slug: string;
  title: string;
  category: string;
  content: string;
}

export const articles: Article[] = [
  { slug: 'conflicts',       title: 'Understanding sync conflicts',           category: 'Syncing',        content: conflicts },
  { slug: 'how-sync-works',  title: 'How syncing works',                      category: 'Syncing',        content: howSyncWorks },
  { slug: 'multi-machine',   title: 'Best practices for multiple machines',   category: 'Syncing',        content: multiMachine },
  { slug: 'glob-patterns',   title: 'Exclude patterns (glob filters)',         category: 'Configuration',  content: globPatterns },
  { slug: 'adding-games',    title: 'Adding games & mapping save folders',     category: 'Configuration',  content: addingGames },
  { slug: 'save-retention',  title: 'Save retention',                         category: 'Configuration',  content: saveRetention },
  { slug: 'agent-update',    title: 'Agent auto-update & fetching from GitHub','category': 'Maintenance', content: agentUpdate },
  { slug: 'troubleshooting', title: 'Troubleshooting',                        category: 'Troubleshooting',content: troubleshooting },
];

export const categories = [...new Set(articles.map(a => a.category))];
