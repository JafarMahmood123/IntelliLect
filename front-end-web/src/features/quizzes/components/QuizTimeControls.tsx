import { useState } from 'react';
import { CheckCircle2, ChevronDown, ChevronRight, TimerReset, UserPlus } from 'lucide-react';
import type { QuizRespondent, QuizTeacher } from '../types';

/** Whole minutes, because that is how a teacher thinks about it mid-lesson. */
const PRESETS = [1, 2, 5];

interface Props {
  quiz: QuizTeacher;
  onExtend: (seconds: number, studentIds?: string[]) => void;
  busy: boolean;
}

/**
 * Adding time to a running quiz — for the room, or for named students.
 *
 * Both exist because they are not the same decision. Extending the class covers "we all need
 * longer"; extending one student covers "they dropped out and lost four minutes", where moving the
 * class deadline would hand the same four minutes to everyone who was fine, including those who
 * have already finished.
 */
export const QuizTimeControls = ({ quiz, onExtend, busy }: Props) => {
  const [pickingStudents, setPickingStudents] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);

  // Only those still working. Someone who has declared themselves finished has no use for more
  // time, and offering it would be an invitation to reopen an answer they have already locked in.
  //
  // Defaulted because a server that predates this field would otherwise take the whole panel down
  // with it — the class deserves a quiz that still runs, minus one control.
  const stillAnswering = (quiz.respondents ?? []).filter((r) => !r.hasSubmitted);

  const toggle = (studentId: string) =>
    setSelected((prev) =>
      prev.includes(studentId) ? prev.filter((id) => id !== studentId) : [...prev, studentId],
    );

  const extendSelected = (minutes: number) => {
    if (selected.length === 0) return;
    onExtend(minutes * 60, selected);
    setSelected([]);
    setPickingStudents(false);
  };

  return (
    <div className="space-y-2 rounded-xl border border-white/5 bg-white/5 p-3">
      <div className="flex items-center gap-2">
        <TimerReset size={13} className="shrink-0 text-slate-400" />
        <p className="text-xs font-bold text-slate-200">Add time</p>
      </div>

      <div className="flex gap-1.5">
        {PRESETS.map((minutes) => (
          <button
            key={minutes}
            type="button"
            disabled={busy}
            onClick={() => onExtend(minutes * 60)}
            className="flex-1 rounded-lg bg-white/5 px-2 py-1.5 text-[11px] font-bold text-slate-200 transition-colors hover:bg-white/10 disabled:opacity-50"
          >
            +{minutes} min
          </button>
        ))}
      </div>
      <p className="text-[10px] text-slate-500">Gives the whole class longer.</p>

      {stillAnswering.length > 0 && (
        <>
          <button
            type="button"
            onClick={() => setPickingStudents((v) => !v)}
            className="flex w-full items-center gap-1.5 border-t border-white/5 pt-2 text-left text-[11px] font-medium text-violet-300"
          >
            {pickingStudents ? (
              <ChevronDown size={12} className="shrink-0" />
            ) : (
              <ChevronRight size={12} className="shrink-0" />
            )}
            <UserPlus size={12} className="shrink-0" />
            Give time to certain students only
          </button>

          {pickingStudents && (
            <div className="space-y-1.5">
              {stillAnswering.map((student) => (
                <StudentRow
                  key={student.studentId}
                  student={student}
                  total={quiz.questions.length}
                  selected={selected.includes(student.studentId)}
                  onToggle={() => toggle(student.studentId)}
                />
              ))}

              <div className="flex gap-1.5 pt-0.5">
                {PRESETS.map((minutes) => (
                  <button
                    key={minutes}
                    type="button"
                    disabled={busy || selected.length === 0}
                    onClick={() => extendSelected(minutes)}
                    className="flex-1 rounded-lg bg-violet-600 px-2 py-1.5 text-[11px] font-bold text-white transition-colors hover:bg-violet-500 disabled:opacity-40"
                  >
                    +{minutes} min
                  </button>
                ))}
              </div>
              <p className="text-[10px] text-slate-500">
                {selected.length === 0
                  ? 'Pick who needs longer. Nobody else is affected.'
                  : `${selected.length} selected. Nobody else is affected.`}
              </p>
            </div>
          )}
        </>
      )}
    </div>
  );
};

const StudentRow = ({
  student,
  total,
  selected,
  onToggle,
}: {
  student: QuizRespondent;
  total: number;
  selected: boolean;
  onToggle: () => void;
}) => (
  <button
    type="button"
    onClick={onToggle}
    className={`flex w-full items-center gap-2 rounded-lg border px-2 py-1.5 text-left text-[11px] transition-colors ${
      selected
        ? 'border-violet-500/50 bg-violet-500/15 text-slate-100'
        : 'border-white/5 bg-slate-900/40 text-slate-300'
    }`}
  >
    {selected ? (
      <CheckCircle2 size={13} className="shrink-0 text-violet-400" />
    ) : (
      <span className="h-3 w-3 shrink-0 rounded-full border border-slate-600" />
    )}
    <span className="min-w-0 flex-1 truncate">{student.studentName}</span>
    {student.hasExtraTime && (
      <span className="shrink-0 text-[10px] font-bold text-amber-400">extra</span>
    )}
    <span className="shrink-0 text-slate-500">
      {student.answeredCount}/{total}
    </span>
  </button>
);
