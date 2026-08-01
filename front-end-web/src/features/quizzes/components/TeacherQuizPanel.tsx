import { useState } from 'react';
import { Plus, Trash2, Send, Clock, Ban, CheckCircle2, Sparkles } from 'lucide-react';
import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useToast } from '../../../components/ui/ToastProvider';
import {
  quizKeys,
  useCancelQuiz,
  useCloseQuiz,
  useCreateQuizDraft,
  useGenerateQuizAnswers,
  useGenerateQuizDraft,
  useGenerateQuizQuestion,
  useOpenQuiz,
  usePublishQuiz,
  useQuizLimits,
  useTeacherQuiz,
  useUpdateQuizDraft,
} from '../hooks/useQuizQueries';
import { formatCountdown, useQuizCountdown } from '../hooks/useQuizCountdown';
import { TeacherQuizSummary } from './TeacherQuizSummary';
import type { QuestionDraft, QuizLimits } from '../types';

interface Props {
  classroomId: string;
  sessionId: string;
  /** See StudentQuizPanel — passed down so this does not open a second SignalR connection. */
  liveEvent: { quizId: string; state: string } | null;
}

const blankQuestion = (limits: QuizLimits): QuestionDraft => ({
  text: '',
  points: 1,
  timeLimitSeconds: limits.defaultSecondsPerQuestion,
  options: Array.from({ length: Math.max(2, limits.minAnswersPerQuestion) }, (_, i) => ({
    text: '',
    isCorrect: i === 0,
  })),
});

/**
 * The teacher's side: compose a quiz mid-session, review it with the correct answers marked, then
 * publish it to the room.
 *
 * Every bound here comes from the server (`useQuizLimits`) rather than a constant, so the composer
 * can never offer a question or option count that publish would reject.
 */
export const TeacherQuizPanel = ({ classroomId, sessionId, liveEvent }: Props) => {
  const { showToast } = useToast();
  const queryClient = useQueryClient();
  const { data: limits, isPending: limitsPending } = useQuizLimits(classroomId);
  const { data: openQuiz } = useOpenQuiz(classroomId, sessionId);
  const { data: liveQuiz } = useTeacherQuiz(classroomId, openQuiz?.id);

  const [draftId, setDraftId] = useState<string | null>(null);
  const [title, setTitle] = useState('');
  const [questions, setQuestions] = useState<QuestionDraft[]>([]);

  const createDraft = useCreateQuizDraft(classroomId, sessionId);
  const updateDraft = useUpdateQuizDraft(classroomId);
  const generate = useGenerateQuizDraft(classroomId, sessionId);
  const generateQuestion = useGenerateQuizQuestion(classroomId, sessionId);
  const generateAnswers = useGenerateQuizAnswers(classroomId, sessionId);
  const [questionCount, setQuestionCount] = useState(3);
  /** Which question is waiting on its answers, so only that card shows a spinner. */
  const [answeringIndex, setAnsweringIndex] = useState<number | null>(null);
  const publish = usePublishQuiz(classroomId, sessionId);
  const close = useCloseQuiz(classroomId, sessionId);
  const cancel = useCancelQuiz(classroomId, sessionId);

  const remaining = useQuizCountdown(liveQuiz?.closesAtUtc, liveQuiz?.serverNowUtc);

  // Keeps the live tally moving for whoever published it, including a second teacher device.
  useEffect(() => {
    if (!liveEvent) return;
    queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
    queryClient.invalidateQueries({ queryKey: quizKeys.detail(liveEvent.quizId) });
    queryClient.invalidateQueries({ queryKey: quizKeys.sessionSummary(sessionId) });
  }, [liveEvent, queryClient, sessionId]);

  if (limitsPending) {
    return <p className="p-4 text-[11px] text-slate-500">Loading…</p>;
  }

  // Distinguished from the pending case on purpose. Guarding on `!limits` alone made a failed
  // request look identical to one still in flight, so an unreachable endpoint sat on "Loading…"
  // indefinitely with nothing in the UI to say the call had already come back 404.
  if (!limits) {
    return (
      <p className="p-4 text-[11px] text-red-400">
        Could not load the quiz composer. The classroom service may be out of date — try again.
      </p>
    );
  }

  // A quiz is already running: show its live state rather than the composer. One at a time keeps
  // the room unambiguous — students would not know which quiz a broadcast referred to otherwise.
  if (liveQuiz && liveQuiz.status === 'Open') {
    return (
      <div className="space-y-3 p-4">
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0">
            <h3 className="truncate text-sm font-bold text-slate-200">{liveQuiz.title || 'Quiz'}</h3>
            <p className="mt-0.5 text-[11px] text-slate-500">
              {liveQuiz.respondentCount} answering · {liveQuiz.submittedCount} finished ·{' '}
              {liveQuiz.totalPoints} marks
            </p>
          </div>
          <div className="flex shrink-0 items-center gap-1.5 rounded-full bg-white/5 px-2.5 py-1 text-[11px] font-bold text-slate-300">
            <Clock size={12} />
            {formatCountdown(remaining)}
          </div>
        </div>

        {liveQuiz.questions.map((question, index) => (
          <div key={question.id} className="rounded-xl border border-white/5 bg-white/5 p-3">
            <p className="text-xs font-medium text-slate-200">
              {index + 1}. {question.text}
            </p>
            <div className="mt-2 space-y-1">
              {question.options.map((option) => (
                <div key={option.id} className="flex items-center justify-between gap-2 text-[11px]">
                  <span
                    className={`flex min-w-0 items-center gap-1.5 ${
                      option.isCorrect ? 'text-emerald-400' : 'text-slate-400'
                    }`}
                  >
                    {option.isCorrect && <CheckCircle2 size={12} className="shrink-0" />}
                    <span className="truncate">{option.text}</span>
                  </span>
                  <span className="shrink-0 text-slate-500">{option.selectedCount}</span>
                </div>
              ))}
            </div>
          </div>
        ))}

        <div className="flex gap-2">
          <button
            type="button"
            onClick={() =>
              close.mutate(liveQuiz.id, {
                onSuccess: () =>
                  queryClient.invalidateQueries({ queryKey: quizKeys.sessionSummary(sessionId) }),
              })
            }
            disabled={close.isPending}
            className="flex-1 rounded-lg bg-violet-600 px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
          >
            Close and mark
          </button>
          <button
            type="button"
            onClick={() =>
              cancel.mutate(liveQuiz.id, {
                onSuccess: () =>
                  queryClient.invalidateQueries({ queryKey: quizKeys.sessionSummary(sessionId) }),
              })
            }
            disabled={cancel.isPending}
            className="flex items-center justify-center gap-1.5 rounded-lg bg-slate-700 px-3 py-1.5 text-xs font-medium text-slate-200 disabled:opacity-50"
          >
            <Ban size={13} />
            Cancel
          </button>
        </div>
        <p className="text-[11px] text-slate-500">
          Cancelling withdraws the quiz from marks. Answers already given are kept, not deleted.
        </p>

        <div className="-mx-4 border-t border-white/5">
          <TeacherQuizSummary classroomId={classroomId} sessionId={sessionId} />
        </div>
      </div>
    );
  }

  const atQuestionLimit = questions.length >= limits.maxQuestionsPerQuiz;
  const totalSeconds = questions.reduce((sum, q) => sum + q.timeLimitSeconds, 0);

  const patch = (index: number, next: Partial<QuestionDraft>) =>
    setQuestions((prev) => prev.map((q, i) => (i === index ? { ...q, ...next } : q)));

  /**
   * 409 and 503 mean genuinely different things and the fix differs: one is "keep teaching", the
   * other is "try again". Collapsing them into one apology would hide the only useful part.
   */
  const reportGenerationError = (error: unknown, what: 'quiz' | 'question' | 'answers') => {
    const status = (error as { response?: { status?: number } })?.response?.status;
    if (status === 409) {
      showToast({
        type: 'error',
        title: 'Nothing to work from yet',
        message:
          'The assistant has not transcribed enough of this session. Keep teaching and try again in a moment.',
      });
      return;
    }
    showToast({
      type: 'error',
      title: `Could not generate ${what === 'answers' ? 'answers' : `a ${what}`}`,
      message: 'The assistant could not do that right now. You can still write it yourself.',
    });
  };

  /**
   * Loads a generated draft into the composer so it can be edited before publishing. The teacher
   * reviews the questions with the correct answers marked, exactly as if they had typed them.
   */
  const runGenerate = async () => {
    try {
      const draft = await generate.mutateAsync(questionCount);
      setDraftId(draft.id);
      setTitle(draft.title);
      setQuestions(
        draft.questions.map((question) => ({
          text: question.text,
          points: question.points,
          timeLimitSeconds: question.timeLimitSeconds,
          options: question.options.map((option) => ({
            text: option.text,
            isCorrect: option.isCorrect,
          })),
        })),
      );
    } catch (error) {
      reportGenerationError(error, 'quiz');
    }
  };

  /** Appends one generated question, telling the assistant what is already there so it varies. */
  const runGenerateQuestion = async () => {
    try {
      const question = await generateQuestion.mutateAsync(
        questions.map((q) => q.text).filter((text) => text.trim()),
      );
      setQuestions((prev) => [...prev, question]);
    } catch (error) {
      reportGenerationError(error, 'question');
    }
  };

  /** Fills one question's options in place. The question text is the teacher's and stays theirs. */
  const runGenerateAnswers = async (index: number) => {
    const question = questions[index];
    if (!question.text.trim()) {
      showToast({
        type: 'error',
        title: 'Write the question first',
        message: 'The assistant needs the question before it can write answers for it.',
      });
      return;
    }
    setAnsweringIndex(index);
    try {
      const generated = await generateAnswers.mutateAsync(question.text);
      patch(index, { options: generated.options });
    } catch (error) {
      reportGenerationError(error, 'answers');
    } finally {
      setAnsweringIndex(null);
    }
  };

  const save = async (publishAfter: boolean) => {
    // Which STEP failed, not which button was pressed. Saving and publishing are two requests, and
    // reporting a failed save as "Could not publish" sends you looking at the questions when the
    // quiz never reached the validation stage at all — it cost real debugging time once already.
    let saved = false;
    try {
      // Generation already created a draft, and so does a first save — updating it keeps one quiz
      // rather than leaving an abandoned draft behind on every save.
      const draft = draftId
        ? await updateDraft.mutateAsync({ quizId: draftId, draft: { title, questions } })
        : await createDraft.mutateAsync({ title, questions });
      setDraftId(draft.id);
      saved = true;
      if (publishAfter) await publish.mutateAsync(draft.id);
    } catch {
      showToast({
        type: 'error',
        title: saved ? 'Could not publish' : 'Could not save',
        message: saved
          ? 'Check every question has text, one correct answer and a mark above zero.'
          : 'Your quiz could not be saved, so nothing was published. Please try again.',
      });
    }
  };

  return (
    <div className="space-y-3 p-4">
      <div>
        <h3 className="text-sm font-bold text-slate-200">New quiz</h3>
        <p className="mt-0.5 text-[11px] text-slate-500">
          Up to {limits.maxQuestionsPerQuiz} questions, {limits.minAnswersPerQuestion}–
          {limits.maxAnswersPerQuestion} answers each. Students see it only once you publish.
        </p>
      </div>

      {/* Generation is the primary way in; typing one by hand stays available below, and is the
          only way to make a quiz at all when the assistant has no transcript to work from. */}
      <div className="space-y-2 rounded-xl border border-violet-500/20 bg-violet-500/10 p-3">
        <div className="flex items-center gap-2">
          <Sparkles size={14} className="shrink-0 text-violet-300" />
          <p className="text-xs font-bold text-slate-200">Generate from your lesson</p>
        </div>
        <p className="text-[11px] text-slate-400">
          Writes questions about the idea you have just been explaining, using this session and your
          course material. You can edit everything before publishing.
        </p>
        <div className="flex items-center gap-2">
          <label className="text-[10px] text-slate-500">
            Questions
            <input
              type="number"
              min={1}
              max={limits.maxQuestionsPerQuiz}
              value={questionCount}
              onChange={(e) => setQuestionCount(Number(e.target.value))}
              className="mt-0.5 w-14 rounded border border-white/10 bg-slate-900/40 px-2 py-1 text-xs text-slate-200 outline-none"
            />
          </label>
          <button
            type="button"
            onClick={runGenerate}
            disabled={generate.isPending}
            className="mt-3 flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-violet-600 px-3 py-2 text-xs font-bold text-white transition-colors hover:bg-violet-500 disabled:opacity-60"
          >
            <Sparkles size={13} />
            {generate.isPending ? 'Writing questions…' : 'Generate'}
          </button>
        </div>
        {generate.isPending && (
          <p className="text-[10px] text-slate-500">
            Reading back what you said and checking it against your material — this takes a few
            seconds.
          </p>
        )}
      </div>

      <input
        value={title}
        onChange={(e) => setTitle(e.target.value)}
        placeholder="Quiz title (optional)"
        className="w-full rounded-lg border border-white/5 bg-slate-900/40 px-3 py-2 text-xs text-slate-200 outline-none"
      />

      {questions.map((question, qi) => (
        <div key={qi} className="space-y-2 rounded-xl border border-white/5 bg-white/5 p-3">
          <div className="flex items-start gap-2">
            <textarea
              value={question.text}
              onChange={(e) => patch(qi, { text: e.target.value })}
              placeholder={`Question ${qi + 1}`}
              rows={2}
              className="min-w-0 flex-1 resize-none rounded-lg border border-white/5 bg-slate-900/40 px-2 py-1.5 text-xs text-slate-200 outline-none"
            />
            <button
              type="button"
              aria-label={`Remove question ${qi + 1}`}
              onClick={() => setQuestions((prev) => prev.filter((_, i) => i !== qi))}
              className="mt-1 shrink-0 text-slate-500 hover:text-red-400"
            >
              <Trash2 size={14} />
            </button>
          </div>

          <div className="flex gap-2">
            <label className="flex-1 text-[10px] text-slate-500">
              Marks
              <input
                type="number"
                min={1}
                value={question.points}
                onChange={(e) => patch(qi, { points: Number(e.target.value) })}
                className="mt-0.5 w-full rounded border border-white/5 bg-slate-900/40 px-2 py-1 text-xs text-slate-200 outline-none"
              />
            </label>
            <label className="flex-1 text-[10px] text-slate-500">
              Seconds
              <input
                type="number"
                min={5}
                value={question.timeLimitSeconds}
                onChange={(e) => patch(qi, { timeLimitSeconds: Number(e.target.value) })}
                className="mt-0.5 w-full rounded border border-white/5 bg-slate-900/40 px-2 py-1 text-xs text-slate-200 outline-none"
              />
            </label>
          </div>

          <div className="space-y-1.5">
            {question.options.map((option, oi) => (
              <div key={oi} className="flex items-center gap-2">
                <button
                  type="button"
                  aria-label={`Mark option ${oi + 1} correct`}
                  onClick={() =>
                    patch(qi, {
                      options: question.options.map((o, i) => ({ ...o, isCorrect: i === oi })),
                    })
                  }
                  className={`shrink-0 ${option.isCorrect ? 'text-emerald-400' : 'text-slate-600'}`}
                >
                  <CheckCircle2 size={15} />
                </button>
                <input
                  value={option.text}
                  onChange={(e) =>
                    patch(qi, {
                      options: question.options.map((o, i) =>
                        i === oi ? { ...o, text: e.target.value } : o,
                      ),
                    })
                  }
                  placeholder={`Answer ${oi + 1}`}
                  className="min-w-0 flex-1 rounded border border-white/5 bg-slate-900/40 px-2 py-1 text-xs text-slate-200 outline-none"
                />
                {question.options.length > limits.minAnswersPerQuestion && (
                  <button
                    type="button"
                    aria-label={`Remove answer ${oi + 1}`}
                    onClick={() =>
                      patch(qi, { options: question.options.filter((_, i) => i !== oi) })
                    }
                    className="shrink-0 text-slate-600 hover:text-red-400"
                  >
                    <Trash2 size={12} />
                  </button>
                )}
              </div>
            ))}

            <div className="flex items-center justify-between gap-2 pt-0.5">
              {question.options.length < limits.maxAnswersPerQuestion ? (
                <button
                  type="button"
                  onClick={() =>
                    patch(qi, { options: [...question.options, { text: '', isCorrect: false }] })
                  }
                  className="text-[11px] text-violet-400"
                >
                  + Add answer
                </button>
              ) : (
                <span />
              )}

              {/* Writes the options for the question the teacher typed, from that question plus
                  the explanation they just gave. Their wording is never touched. */}
              <button
                type="button"
                onClick={() => runGenerateAnswers(qi)}
                disabled={answeringIndex !== null}
                className="flex shrink-0 items-center gap-1 rounded-lg bg-violet-500/15 px-2 py-1 text-[11px] font-bold text-violet-300 transition-colors hover:bg-violet-500/25 disabled:opacity-50"
              >
                <Sparkles size={11} />
                {answeringIndex === qi ? 'Writing…' : 'Generate answers'}
              </button>
            </div>
          </div>
        </div>
      ))}

      <div className="flex gap-2">
        <button
          type="button"
          disabled={atQuestionLimit}
          onClick={() => setQuestions((prev) => [...prev, blankQuestion(limits)])}
          className="flex flex-1 items-center justify-center gap-1.5 rounded-lg border border-dashed border-white/10 px-3 py-2 text-xs text-slate-300 disabled:opacity-40"
        >
          <Plus size={14} />
          {atQuestionLimit ? `Limit is ${limits.maxQuestionsPerQuiz}` : 'Add question'}
        </button>

        {/* Appends one generated question. It is told what is already in the composer, so pressing
            it repeatedly varies rather than restating the same point. */}
        <button
          type="button"
          disabled={atQuestionLimit || generateQuestion.isPending}
          onClick={runGenerateQuestion}
          className="flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-violet-500/15 px-3 py-2 text-xs font-bold text-violet-300 transition-colors hover:bg-violet-500/25 disabled:opacity-40"
        >
          <Sparkles size={14} />
          {generateQuestion.isPending ? 'Writing…' : 'Generate question'}
        </button>
      </div>

      {questions.length > 0 && (
        <>
          <p className="text-[11px] text-slate-500">
            Total time {formatCountdown(totalSeconds)} · {questions.reduce((s, q) => s + q.points, 0)} marks
          </p>
          <button
            type="button"
            onClick={() => save(true)}
            disabled={publish.isPending || createDraft.isPending}
            className="flex w-full items-center justify-center gap-1.5 rounded-lg bg-violet-600 px-3 py-2 text-xs font-medium text-white disabled:opacity-50"
          >
            <Send size={14} />
            Publish to students
          </button>
        </>
      )}

      {/* Marks so far, live. Also what the teacher sees once a quiz has been closed. */}
      <div className="-mx-4 border-t border-white/5">
        <TeacherQuizSummary classroomId={classroomId} sessionId={sessionId} />
      </div>
    </div>
  );
};
