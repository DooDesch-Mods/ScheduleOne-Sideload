import type { ReactNode } from 'react';

export function Card({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <section className="rounded-xl border border-hairline bg-panel p-4">
      <h2 className="text-sm font-semibold">{title}</h2>
      {note && <p className="mb-3 text-xs text-muted">{note}</p>}
      {children}
    </section>
  );
}

export function Button({
  children,
  onClick,
  tone = 'normal',
}: {
  children: ReactNode;
  onClick?: () => void;
  tone?: 'normal' | 'accent' | 'danger';
}) {
  const tones = {
    normal: 'bg-raised text-ink hover:bg-hairline',
    accent: 'bg-accent text-white hover:opacity-90',
    danger: 'bg-bad text-white hover:opacity-90',
  };

  return (
    <button onClick={onClick} className={`rounded-lg px-3 py-2 text-sm ${tones[tone]}`}>
      {children}
    </button>
  );
}
