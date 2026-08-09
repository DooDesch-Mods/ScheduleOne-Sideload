import { useState } from 'react';
import { Layout } from './panels/layout';
import { Components } from './panels/components';
import { Data } from './panels/data';
import { Bridge } from './panels/bridge';

const TABS = [
  { id: 'layout', label: 'Layout', panel: Layout },
  { id: 'components', label: 'Parts', panel: Components },
  { id: 'data', label: 'Data', panel: Data },
  { id: 'bridge', label: 'Bridge', panel: Bridge },
] as const;

export function App() {
  const [tab, setTab] = useState<string>('layout');
  const Panel = TABS.find((t) => t.id === tab)?.panel ?? Layout;

  return (
    <div className="flex h-full flex-col bg-canvas text-ink">
      <header className="flex items-center justify-between border-b border-hairline bg-panel px-4 py-3">
        <div>
          <h1 className="text-base font-semibold">Sideload showcase</h1>
          <p className="text-xs text-muted">React 19, Tailwind v4, one bundle</p>
        </div>
        <span className="rounded-full bg-accent-soft px-2.5 py-1 text-xs text-accent">live</span>
      </header>

      <nav className="flex gap-1 border-b border-hairline bg-panel px-2 py-2">
        {TABS.map((t) => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            className={
              'flex-1 rounded-lg px-3 py-2 text-sm ' +
              (t.id === tab ? 'bg-accent text-white' : 'text-muted hover:bg-raised hover:text-ink')
            }
          >
            {t.label}
          </button>
        ))}
      </nav>

      <main className="flex-1 overflow-y-auto p-4">
        <Panel />
      </main>
    </div>
  );
}
