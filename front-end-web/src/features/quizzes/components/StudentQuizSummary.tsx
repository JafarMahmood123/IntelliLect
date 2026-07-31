import { Ban, Clock } from 'lucide-react';
import { useMySessionQuizSummary } from '../hooks/useQuizQueries';

interface Props {
  classroomId: string;
  sessionId: string;
}

/**
 * A student's own marks for the session — their quizzes and their total. Shows nothing about anyone
 * else, and no marks for a quiz still open, because answers can be changed until it closes.
 *
 * Independent of the session being live, so the same component serves the in-session view and the
 * one opened afterwards.
 */
export const StudentQuizSummary = ({ classroomId, sessionId }: Props) => {
  const { data, isPending, isError } = useMySessionQuizSummary(classroomId, sessionId);

  if (isPending) return <p className="p-4 text-[11px] text-slate-500">Loading your marks…</p>;
  if (isError) return <p className="p-4 text-[11px] text-red-400">Could not load your marks.</p>;

  if (data.quizzes.length === 0) {
    return (
      <div className="p-4">
        <p className="text-sm font-medium text-slate-300">No quizzes yet</p>
        <p className="mt-1 text-[11px] text-slate-500">
          Your marks will appear here after you answer one.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3 p-4">
      <div className="rounded-xl border border-violet-500/20 bg-violet-500/10 p-3">
        <p className="text-[11px] text-slate-400">Your total this session</p>
        <p className="mt-0.5 text-lg font-bold text-slate-100">
          {data.score}
          <span className="text-sm text-slate-500">/{data.totalPointsAvailable}</span>
          <span className="ml-2 text-sm font-bold text-violet-400">{data.percentage}%</span>
        </p>
      </div>

      <div className="space-y-1.5">
        {data.quizzes.map((quiz) => {
          const stillOpen = quiz.status === 'Open';
          return (
            <div
              key={quiz.quizId}
              className={`rounded-xl border p-3 ${
                quiz.countsTowardsMarks
                  ? 'border-white/5 bg-white/5'
                  : 'border-amber-500/20 bg-amber-500/5'
              }`}
            >
              <div className="flex items-start justify-between gap-2">
                <p className="min-w-0 truncate text-xs font-medium text-slate-200">
                  {quiz.title || 'Quiz'}
                </p>
                {stillOpen ? (
                  <span className="flex shrink-0 items-center gap-1 text-[10px] text-slate-400">
                    <Clock size={10} />
                    In progress
                  </span>
                ) : (
                  <span className="shrink-0 text-xs font-bold text-slate-100">
                    {quiz.score}
                    <span className="text-slate-500">/{quiz.totalPoints}</span>
                  </span>
                )}
              </div>

              <p className="mt-0.5 text-[10px] text-slate-500">
                {quiz.answeredCount} of {quiz.questionCount} answered
              </p>

              {!quiz.countsTowardsMarks && (
                <p className="mt-1 flex items-center gap-1 text-[10px] text-amber-400">
                  <Ban size={10} />
                  Cancelled by your teacher — does not count
                </p>
              )}

              {stillOpen && (
                <p className="mt-1 text-[10px] text-slate-500">
                  Your marks appear once the teacher closes this quiz.
                </p>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};
