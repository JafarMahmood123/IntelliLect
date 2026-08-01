import { useState } from 'react';
import { Calendar, ChevronLeft } from 'lucide-react';
import { useClassroomSessions } from '../../classrooms/hooks/useClassroomQueries';
import { StudentQuizSummary } from './StudentQuizSummary';
import { TeacherQuizSummary } from './TeacherQuizSummary';
import { QuizTrackingPanel } from './QuizTrackingPanel';

interface Props {
  classroomId: string;
  isTeacher: boolean;
}

/**
 * Quiz marks, outside a live session.
 *
 * The in-session sidebar carries the same two summaries, but it disappears the moment the session
 * ends — and a student's review is only released once the teacher closes the quiz, which is often
 * the last thing that happens. Without this, marks would be visible only in the minutes between.
 *
 * Marks are per session, so this is a session picker first and a summary second.
 */
export const QuizMarksPanel = ({ classroomId, isTeacher }: Props) => {
  const { data: sessions = [], isLoading } = useClassroomSessions(classroomId);
  const [openSessionId, setOpenSessionId] = useState<string | null>(null);

  if (isLoading) {
    return <div className="p-8 text-center text-sm text-slate-500">Loading sessions…</div>;
  }

  if (sessions.length === 0) {
    return (
      <div className="py-12 text-center">
        <Calendar className="mx-auto mb-4 text-slate-300" size={48} />
        <p className="text-sm italic text-slate-500">No sessions yet, so there are no marks.</p>
      </div>
    );
  }

  const open = sessions.find((session) => session.id === openSessionId);

  // Cumulative first, one lesson second. "How is this going" is the question someone opening a
  // marks tab has; "how did that one lesson go" is the follow-up they drill into.
  const overview = !open && (
    <div className="mb-6 border-b border-slate-200 pb-6 dark:border-slate-800">
      <QuizTrackingPanel classroomId={classroomId} isTeacher={isTeacher} />
    </div>
  );

  if (open) {
    return (
      <div className="space-y-4">
        <button
          type="button"
          onClick={() => setOpenSessionId(null)}
          className="flex items-center gap-1.5 text-sm font-medium text-slate-500 transition-colors hover:text-slate-900 dark:hover:text-white"
        >
          <ChevronLeft size={16} />
          All sessions
        </button>

        <div>
          <h3 className="font-bold text-slate-900 dark:text-white">{open.title}</h3>
          <p className="mt-0.5 text-xs text-slate-500">
            {new Date(open.scheduledAtUtc).toLocaleString([], {
              dateStyle: 'medium',
              timeStyle: 'short',
            })}
          </p>
        </div>

        {/* The summaries are built for the session sidebar and are dark by design. Kept that way
            rather than re-themed, so the marks a teacher sees mid-session and afterwards are the
            same component and cannot drift apart. */}
        <div className="overflow-hidden rounded-2xl border border-slate-800 bg-slate-900">
          {isTeacher ? (
            <TeacherQuizSummary classroomId={classroomId} sessionId={open.id} />
          ) : (
            <StudentQuizSummary classroomId={classroomId} sessionId={open.id} />
          )}
        </div>
      </div>
    );
  }

  return (
    <div>
      {overview}
      <h3 className="mb-2 text-sm font-bold text-slate-900 dark:text-white">
        {isTeacher ? 'Marks by session' : 'Your marks by session'}
      </h3>
      <div className="grid gap-3">
        {sessions.map((session) => (
        <button
          key={session.id}
          type="button"
          onClick={() => setOpenSessionId(session.id)}
          className="flex items-center justify-between rounded-2xl border border-slate-200 bg-white p-4 text-left transition-colors hover:border-violet-400 dark:border-slate-800 dark:bg-slate-900/50 dark:hover:border-violet-500"
        >
          <div className="min-w-0">
            <h4 className="truncate font-bold text-slate-900 dark:text-white">{session.title}</h4>
            <p className="mt-0.5 text-xs text-slate-500">
              {new Date(session.scheduledAtUtc).toLocaleString([], {
                dateStyle: 'medium',
                timeStyle: 'short',
              })}
            </p>
          </div>
          <span className="shrink-0 text-xs font-bold text-violet-500">
            {isTeacher ? 'View marks' : 'My marks'}
          </span>
        </button>
        ))}
      </div>
    </div>
  );
};
