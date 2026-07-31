import { Trophy, Ban, CheckCircle2 } from 'lucide-react';
import { useSessionQuizSummary } from '../hooks/useQuizQueries';

interface Props {
  classroomId: string;
  sessionId: string;
}

/**
 * The teacher's marks for the whole session: who scored what, and how the class handled each
 * question.
 *
 * Session-scoped rather than per-quiz because that is the question a teacher actually asks. Nothing
 * here depends on the session being live, so the same component serves the in-session view and the
 * one opened after the session has ended.
 */
export const TeacherQuizSummary = ({ classroomId, sessionId }: Props) => {
  const { data, isPending, isError } = useSessionQuizSummary(classroomId, sessionId);

  if (isPending) return <p className="p-4 text-[11px] text-slate-500">Loading marks…</p>;
  if (isError) return <p className="p-4 text-[11px] text-red-400">Could not load marks.</p>;

  if (data.quizCount === 0) {
    return (
      <div className="p-4">
        <p className="text-sm font-medium text-slate-300">No quizzes yet</p>
        <p className="mt-1 text-[11px] text-slate-500">
          Marks appear here as soon as you publish one.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4 p-4">
      <div>
        <h3 className="text-sm font-bold text-slate-200">Session marks</h3>
        <p className="mt-0.5 text-[11px] text-slate-500">
          {data.quizCount} quiz(zes) · {data.totalPointsAvailable} marks available
          {data.countedQuizCount !== data.quizCount &&
            ` · ${data.quizCount - data.countedQuizCount} cancelled, not counted`}
        </p>
      </div>

      {/* --- who scored what --- */}
      <div className="space-y-1.5">
        {data.students.length === 0 ? (
          <p className="text-[11px] text-slate-500">Nobody has answered yet.</p>
        ) : (
          data.students.map((student, index) => (
            <div
              key={student.studentId}
              className="flex items-center gap-3 rounded-xl border border-white/5 bg-white/5 p-2.5"
            >
              <span className="w-5 shrink-0 text-center text-[11px] font-bold text-slate-500">
                {index === 0 ? <Trophy size={13} className="mx-auto text-amber-400" /> : index + 1}
              </span>
              <span className="min-w-0 flex-1 truncate text-xs font-medium text-slate-200">
                {student.studentName}
              </span>
              <span className="shrink-0 text-[11px] text-slate-500">
                {student.correctCount}/{student.answeredCount} correct
              </span>
              <span className="shrink-0 text-xs font-bold text-slate-100">
                {student.score}
                <span className="text-slate-500">/{student.totalPointsAvailable}</span>
              </span>
              <span className="w-9 shrink-0 text-right text-[11px] font-bold text-violet-400">
                {student.percentage}%
              </span>
            </div>
          ))
        )}
      </div>

      {/* --- how each question went --- */}
      <div>
        <h4 className="text-xs font-bold text-slate-300">Questions</h4>
        <p className="mt-0.5 text-[11px] text-slate-500">
          A wrong answer taking most of the votes usually means a misconception worth revisiting.
        </p>
      </div>

      <div className="space-y-2">
        {data.questions.map((question, index) => (
          <div
            key={question.questionId}
            className={`rounded-xl border p-3 ${
              question.countsTowardsMarks
                ? 'border-white/5 bg-white/5'
                : 'border-amber-500/20 bg-amber-500/5'
            }`}
          >
            <div className="flex items-start justify-between gap-2">
              <p className="min-w-0 text-xs font-medium leading-snug text-slate-200">
                {index + 1}. {question.text}
              </p>
              <span className="shrink-0 text-[10px] text-slate-500">{question.points} marks</span>
            </div>

            {!question.countsTowardsMarks && (
              <p className="mt-1 flex items-center gap-1 text-[10px] text-amber-400">
                <Ban size={10} />
                Cancelled — answers kept, not counted
              </p>
            )}

            <p className="mt-1 text-[10px] text-slate-500">
              {question.correctCount} of {question.answeredCount} answered correctly
            </p>

            <div className="mt-2 space-y-1">
              {question.options.map((option) => {
                const share =
                  question.answeredCount > 0
                    ? Math.round((option.selectedCount / question.answeredCount) * 100)
                    : 0;
                return (
                  <div key={option.optionId} className="flex items-center gap-2 text-[11px]">
                    <span
                      className={`flex min-w-0 flex-1 items-center gap-1.5 ${
                        option.isCorrect ? 'text-emerald-400' : 'text-slate-400'
                      }`}
                    >
                      {option.isCorrect && <CheckCircle2 size={11} className="shrink-0" />}
                      <span className="truncate">{option.text}</span>
                    </span>
                    <span className="h-1.5 w-16 shrink-0 overflow-hidden rounded-full bg-slate-800">
                      <span
                        className={`block h-full ${
                          option.isCorrect ? 'bg-emerald-500' : 'bg-slate-600'
                        }`}
                        style={{ width: `${share}%` }}
                      />
                    </span>
                    <span className="w-6 shrink-0 text-right text-slate-500">
                      {option.selectedCount}
                    </span>
                  </div>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};
