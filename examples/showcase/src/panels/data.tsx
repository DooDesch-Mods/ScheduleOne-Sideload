import { useMemo, useState } from 'react';
import { Card } from '../ui';

type Row = { name: string; kind: string; value: number };

const ROWS: Row[] = [
  { name: 'Sour Diesel', kind: 'seed', value: 36 },
  { name: 'Green Crack', kind: 'seed', value: 42 },
  { name: 'Granddaddy Purple', kind: 'seed', value: 58 },
  { name: 'Baggie', kind: 'packaging', value: 1 },
  { name: 'Jar', kind: 'packaging', value: 3 },
  { name: 'Brick Press', kind: 'tool', value: 400 },
  { name: 'Mixing Station', kind: 'tool', value: 500 },
  { name: 'Cash', kind: 'money', value: 1200 },
];

export function Data() {
  const [query, setQuery] = useState('');
  const [sort, setSort] = useState<'name' | 'value'>('value');

  const rows = useMemo(() => {
    const needle = query.trim().toLowerCase();
    return ROWS.filter((r) => !needle || r.name.toLowerCase().includes(needle)).sort((a, b) =>
      sort === 'name' ? a.name.localeCompare(b.name) : b.value - a.value,
    );
  }, [query, sort]);

  return (
    <div className="flex flex-col gap-4">
      <Card title="Filter and sort" note="A list that re-renders on every keystroke">
        <input
          value={query}
          onInput={(e) => setQuery((e.target as HTMLInputElement).value)}
          placeholder="filter"
          className="rounded-lg border border-hairline bg-sunken px-3 py-2 text-sm"
        />
        <div className="mt-2 flex gap-1">
          {(['value', 'name'] as const).map((key) => (
            <button
              key={key}
              onClick={() => setSort(key)}
              className={
                'rounded-md px-2.5 py-1 text-xs ' + (sort === key ? 'bg-accent text-white' : 'bg-raised text-muted')
              }
            >
              by {key}
            </button>
          ))}
        </div>
      </Card>

      <div className="flex flex-col gap-1.5">
        {rows.map((row) => (
          <div
            key={row.name}
            className="flex items-center justify-between rounded-lg border border-hairline bg-panel px-3 py-2.5"
          >
            <div>
              <div className="text-sm">{row.name}</div>
              <div className="text-xs text-muted">{row.kind}</div>
            </div>
            <div className="text-sm tabular-nums text-accent">${row.value}</div>
          </div>
        ))}

        {rows.length === 0 && <p className="py-6 text-center text-sm text-muted">nothing matches "{query}"</p>}
      </div>
    </div>
  );
}
