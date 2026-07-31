import React from 'react';
import { ChevronRight } from 'lucide-react';

export interface SidebarSection {
  id: string;
  label: string;
  /** One line on what the section is for, so the menu explains itself without being opened. */
  description: string;
  icon: React.ReactNode;
}

interface Props {
  sections: SidebarSection[];
  onOpen: (id: string) => void;
}

/**
 * The drawer's landing view: every section the current role may open, as a full-width button.
 *
 * This replaced a tab strip along the top. Four tabs never fit across a 320px drawer — they
 * overflowed into a sideways scrollbar that hid Session Settings past the edge — and the strip only
 * gets tighter as sections are added. A vertical list has the opposite property: it has room for
 * a label AND a description, and another section costs nothing.
 */
export const SidebarMenu = ({ sections, onOpen }: Props) => (
  <nav aria-label="Session panels" className="space-y-2 p-3">
    {sections.map((section) => (
      <button
        key={section.id}
        type="button"
        onClick={() => onOpen(section.id)}
        className="flex w-full items-center gap-3 rounded-xl border border-white/5 bg-white/5 p-3 text-left outline-none transition-colors hover:border-violet-500/30 hover:bg-white/10 focus-visible:border-violet-500"
      >
        <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-violet-500/15 text-violet-300">
          {section.icon}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm font-bold text-slate-200">{section.label}</span>
          <span className="block truncate text-[11px] text-slate-500">{section.description}</span>
        </span>
        <ChevronRight size={16} className="shrink-0 text-slate-600" />
      </button>
    ))}
  </nav>
);
