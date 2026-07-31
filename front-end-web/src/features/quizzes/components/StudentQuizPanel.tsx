import { useEffect, useState } from 'react';
import { Clock, CheckCircle2 } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { useToast } from '../../../components/ui/ToastProvider';
import { quizKeys, useOpenQuiz, useSubmitQuizAnswer } from '../hooks/useQuizQueries';
import { formatCountdown, useQuizCountdown } from '../hooks/useQuizCountdown';
import { StudentQuizSummary } from './StudentQuizSummary';
import type { QuizStudent } from '../types';

interface Props {
  classroomId: string;
  sessionId: string;
  /**
   * Latest quiz state pushed over the hub, passed down rather than subscribed to here —
   * useStreamHub opens a SignalR connection per call, and the sidebar already has one.
   */
  liveEvent: { quizId: string; state: string } | null;
}

/**
 * The student's side: answer the open quiz, if there is one.
 *
 * Seeded from the "open quiz for this session" endpoint rather than from the live broadcast alone,
 * so a student who joined after the teacher published still gets it.
 */
export const StudentQuizPanel = ({ classroomId, sessionId, liveEvent }: Props) => {
  const { data: quiz, isPending } = useOpenQuiz(classroomId, sessionId);
  const queryClient = useQueryClient();

  // The broadcast carries an id and a state, never the quiz itself, so this refetches rather than
  // trusting the wire. That is what keeps the answer key off the socket.
  useEffect(() => {
    if (!liveEvent) return;
    queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
    queryClient.invalidateQueries({ queryKey: quizKeys.studentView(liveEvent.quizId) });
    queryClient.invalidateQueries({ queryKey: quizKeys.mySessionSummary(sessionId) });
  }, [liveEvent, queryClient, sessionId]);

  if (isPending) {
    return <p className="p-4 text-[11px] text-slate-500">Checking for a quiz…</p>;
  }

  // The summary sits below whatever is happening now, so a student can always see where they
  // stand — mid-quiz, between quizzes, and after the session ends.
  return (
    <>
      {quiz ? (
        <StudentQuizForm classroomId={classroomId} quiz={quiz} />
      ) : (
        <div className="p-4 pb-0">
          <p className="text-sm font-medium text-slate-300">No quiz running</p>
          <p className="mt-1 text-[11px] text-slate-500">
            When your teacher starts one it will appear here.
          </p>
        </div>
      )}
      <div className="border-t border-white/5">
        <StudentQuizSummary classroomId={classroomId} sessionId={sessionId} />
      </div>
    </>
  );
};

const StudentQuizForm = ({ classroomId, quiz }: { classroomId: string; quiz: QuizStudent }) => {
  const { showToast } = useToast();
  const { mutate } = useSubmitQuizAnswer(classroomId, quiz.id);
  const remaining = useQuizCountdown(quiz.closesAtUtc, quiz.serverNowUtc);

  // Selections are held locally so the UI responds instantly; the server is the record of truth
  // and is re-read whenever the quiz is reloaded.
  const [selected, setSelected] = useState<Record<string, string>>({});
  useEffect(() => {
    const initial: Record<string, string> = {};
    for (const q of quiz.questions) {
      if (q.selectedOptionId) initial[q.id] = q.selectedOptionId;
    }
    setSelected(initial);
  }, [quiz]);

  const timeUp = remaining === 0;

  const choose = (questionId: string, optionId: string) => {
    const previous = selected[questionId];
    setSelected((prev) => ({ ...prev, [questionId]: optionId }));

    mutate(
      { questionId, optionId },
      {
        onError: () => {
          // Put the UI back where the server actually is, rather than leaving a selection the
          // server never accepted — most likely because time ran out.
          setSelected((prev) => {
            const next = { ...prev };
            if (previous) next[questionId] = previous;
            else delete next[questionId];
            return next;
          });
          showToast({
            type: 'error',
            title: 'Answer not saved',
            message: 'It may be past the deadline. Please try again.',
          });
        },
      },
    );
  };

  const answered = Object.keys(selected).length;

  return (
    <div className="space-y-4 p-4">
      <div className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate text-sm font-bold text-slate-200">{quiz.title || 'Quiz'}</h3>
          <p className="mt-0.5 text-[11px] text-slate-500">
            {answered} of {quiz.questions.length} answered · {quiz.totalPoints} marks
          </p>
        </div>
        <div
          className={`flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-[11px] font-bold ${
            timeUp ? 'bg-red-500/15 text-red-400' : 'bg-white/5 text-slate-300'
          }`}
        >
          <Clock size={12} />
          {timeUp ? 'Time up' : formatCountdown(remaining)}
        </div>
      </div>

      {quiz.questions.map((question, index) => (
        <div key={question.id} className="rounded-xl border border-white/5 bg-white/5 p-3">
          <p className="text-sm font-medium leading-snug text-slate-200">
            {index + 1}. {question.text}
          </p>
          <p className="mt-0.5 text-[10px] text-slate-500">{question.points} marks</p>

          <div className="mt-2 space-y-1.5">
            {question.options.map((option) => {
              const isChosen = selected[question.id] === option.id;
              return (
                <button
                  key={option.id}
                  type="button"
                  disabled={timeUp}
                  onClick={() => choose(question.id, option.id)}
                  className={`flex w-full items-center gap-2 rounded-lg border px-3 py-2 text-left text-xs transition-colors disabled:opacity-50 ${
                    isChosen
                      ? 'border-violet-500/50 bg-violet-500/15 text-slate-100'
                      : 'border-white/5 bg-slate-900/40 text-slate-300'
                  }`}
                >
                  {isChosen ? (
                    <CheckCircle2 size={14} className="shrink-0 text-violet-400" />
                  ) : (
                    <span className="h-3.5 w-3.5 shrink-0 rounded-full border border-slate-600" />
                  )}
                  <span className="min-w-0">{option.text}</span>
                </button>
              );
            })}
          </div>
        </div>
      ))}

      <p className="text-[11px] text-slate-500">
        You can change an answer until the time runs out. Your marks appear once the teacher closes
        the quiz.
      </p>
    </div>
  );
};
