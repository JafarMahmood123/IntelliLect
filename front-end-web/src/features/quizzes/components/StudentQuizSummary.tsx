import { useState } from 'react';
import { Ban, Check, ChevronDown, ChevronRight, Circle, Clock, X } from 'lucide-react';
import { useMySessionQuizSummary } from '../hooks/useQuizQueries';
import type { MyQuestionReview, MyQuizScore } from '../types';

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
          <span className="ms-2 text-sm font-bold text-violet-400">{data.percentage}%</span>
        </p>
      </div>

      <div className="space-y-1.5">
        {data.quizzes.map((quiz) => (
          <QuizRow key={quiz.quizId} quiz={quiz} />
        ))}
      </div>
    </div>
  );
};

/**
 * One quiz, expandable into the question-by-question review.
 *
 * Collapsed by default: a student opening their marks wants the total first, and a session with
 * several quizzes would otherwise be a wall of questions to scroll past.
 */
const QuizRow = ({ quiz }: { quiz: MyQuizScore }) => {
  const [open, setOpen] = useState(false);
  const stillOpen = quiz.status === 'Open';
  // Server-gated, not a local guess: the review is empty until the quiz is finished, so there is
  // nothing to expand into while it is still running.
  const reviewable = quiz.questions.length > 0;

  return (
    <div
      className={`rounded-xl border ${
        quiz.countsTowardsMarks ? 'border-white/5 bg-white/5' : 'border-amber-500/20 bg-amber-500/5'
      }`}
    >
      <button
        type="button"
        disabled={!reviewable}
        onClick={() => setOpen((v) => !v)}
        className="w-full p-3 text-start disabled:cursor-default"
      >
        <div className="flex items-start justify-between gap-2">
          <p className="flex min-w-0 items-center gap-1 text-xs font-medium text-slate-200">
            {reviewable &&
              (open ? (
                <ChevronDown size={12} className="shrink-0 text-slate-500" />
              ) : (
                <ChevronRight size={12} className="shrink-0 text-slate-500" />
              ))}
            <span className="truncate">{quiz.title || 'Quiz'}</span>
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
          {reviewable && !open && ' · tap to see the answers'}
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
      </button>

      {open && (
        <div className="space-y-2 border-t border-white/5 p-3">
          {quiz.questions.map((question, index) => (
            <QuestionReview key={question.questionId} question={question} index={index} />
          ))}
        </div>
      )}
    </div>
  );
};

/**
 * What was asked, what the student picked, and what was actually right.
 *
 * Every option is shown rather than just the two that matter, because the wrong options are the
 * question — seeing only "you said A, the answer was C" leaves nothing to learn from.
 */
const QuestionReview = ({ question, index }: { question: MyQuestionReview; index: number }) => {
  const skipped = question.selectedOptionId === null;

  return (
    <div className="rounded-lg border border-white/5 bg-slate-900/40 p-2.5">
      <div className="flex items-start justify-between gap-2">
        <p className="min-w-0 text-[11px] font-medium leading-snug text-slate-200">
          {index + 1}. {question.text}
        </p>
        <span
          className={`shrink-0 text-[10px] font-bold ${
            question.isCorrect ? 'text-emerald-400' : 'text-slate-500'
          }`}
        >
          {question.pointsAwarded}/{question.points}
        </span>
      </div>

      {skipped && (
        <p className="mt-1 text-[10px] text-amber-400">You did not answer this one.</p>
      )}

      <div className="mt-2 space-y-1">
        {question.options.map((option) => {
          const chosen = option.optionId === question.selectedOptionId;
          // Four states, and they must stay distinguishable: right and chosen, right but missed,
          // wrong and chosen, wrong and ignored. Colour alone does not say which is which, so each
          // carries its own icon.
          const tone = option.isCorrect
            ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-300'
            : chosen
              ? 'border-red-500/30 bg-red-500/10 text-red-300'
              : 'border-transparent text-slate-400';

          return (
            <div
              key={option.optionId}
              className={`flex items-center gap-2 rounded-md border px-2 py-1.5 text-[11px] ${tone}`}
            >
              {option.isCorrect ? (
                <Check size={12} className="shrink-0" />
              ) : chosen ? (
                <X size={12} className="shrink-0" />
              ) : (
                <Circle size={8} className="ms-0.5 me-0.5 shrink-0 text-slate-600" />
              )}
              <span className="min-w-0 flex-1">{option.text}</span>
              {chosen && (
                <span className="shrink-0 text-[9px] font-bold uppercase tracking-wide opacity-70">
                  Your answer
                </span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};
