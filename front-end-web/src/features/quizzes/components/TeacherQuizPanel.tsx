import { useState } from 'react';
import {
  AlertTriangle,
  Ban,
  BookOpen,
  CheckCircle2,
  Clock,
  Plus,
  Save,
  Send,
  Sparkles,
  Trash2,
} from 'lucide-react';
import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useToast } from '../../../components/ui/ToastProvider';
import {
  quizKeys,
  useCancelQuiz,
  useCloseQuiz,
  useCreateQuizDraft,
  useExtendQuiz,
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
import { useQuizCloseWatch } from '../hooks/useQuizCloseWatch';
import { TeacherQuizSummary } from './TeacherQuizSummary';
import { QuizTimeControls } from './QuizTimeControls';
import type { QuestionDraft, QuizCorrection, QuizLimits } from '../types';

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
  /**
   * Where the course material contradicted what the teacher said. Held in composer state rather
   * than fetched, because it belongs to the act of GENERATION and not to the quiz — reopening a
   * draft later must not re-accuse the teacher of a mistake they have already dealt with.
   */
  const [corrections, setCorrections] = useState<QuizCorrection[]>([]);

  /**
   * Publishing hands the quiz to the room. It stops being the teacher's to edit at that moment, so
   * it must leave the composer with it — otherwise the next press of Publish tries to rewrite a
   * quiz the server will refuse to change, and reports it as a save failure.
   */
  const resetComposer = () => {
    setDraftId(null);
    setTitle('');
    setQuestions([]);
    setCorrections([]);
  };

  // Watches the draft the composer is holding. Resetting on publish alone is not enough: the same
  // quiz can be published from a second device, and a lost publish response would leave this
  // composer editing something that has already gone out to the class.
  const { data: draftQuiz } = useTeacherQuiz(classroomId, draftId ?? undefined);
  const draftWasPublished = Boolean(draftQuiz && draftQuiz.status !== 'Draft');
  useEffect(() => {
    if (draftWasPublished) resetComposer();
  }, [draftWasPublished]);

  const createDraft = useCreateQuizDraft(classroomId, sessionId);
  const updateDraft = useUpdateQuizDraft(classroomId);
  const generate = useGenerateQuizDraft(classroomId, sessionId);
  const generateQuestion = useGenerateQuizQuestion(classroomId, sessionId);
  const generateAnswers = useGenerateQuizAnswers(classroomId, sessionId);
  const [questionCount, setQuestionCount] = useState(3);
  /** A whole lesson is worth more questions than one explanation, so the two counts are separate. */
  const [fullQuestionCount, setFullQuestionCount] = useState(10);
  /** Which of the two generators is running, so only that button shows its own progress. */
  const [generating, setGenerating] = useState<'quick' | 'full' | null>(null);
  /** Which question is waiting on its answers, so only that card shows a spinner. */
  const [answeringIndex, setAnsweringIndex] = useState<number | null>(null);
  const publish = usePublishQuiz(classroomId, sessionId);
  const close = useCloseQuiz(classroomId, sessionId);
  const extend = useExtendQuiz(classroomId, sessionId);
  const cancel = useCancelQuiz(classroomId, sessionId);

  const remaining = useQuizCountdown(liveQuiz?.closesAtUtc, liveQuiz?.serverNowUtc);
  // The server closes a timed-out quiz on its own; this re-reads until it has, so the panel hands
  // the composer back without the teacher pressing anything.
  useQuizCloseWatch(sessionId, liveQuiz?.id, liveQuiz?.status === 'Open' && remaining === 0);

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
          <div
            className={`flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-[11px] font-bold ${
              remaining === 0 ? 'bg-amber-500/15 text-amber-300' : 'bg-white/5 text-slate-300'
            }`}
          >
            <Clock size={12} />
            {remaining === 0 ? 'Marking…' : formatCountdown(remaining)}
          </div>
        </div>

        {remaining === 0 && (
          <p className="text-[11px] text-amber-300/80">
            Time is up. The quiz is closing on its own and marks are being released — add time below
            if the class needs longer.
          </p>
        )}

        <QuizTimeControls
          quiz={liveQuiz}
          onExtend={(seconds, studentIds) =>
            extend.mutate(
              { quizId: liveQuiz.id, seconds, studentIds },
              {
                onSuccess: () =>
                  showToast({
                    type: 'success',
                    title: 'Time added',
                    message: studentIds?.length
                      ? `${studentIds.length} student(s) have ${Math.round(seconds / 60)} more minute(s).`
                      : `The class has ${Math.round(seconds / 60)} more minute(s).`,
                  }),
                onError: () =>
                  showToast({
                    type: 'error',
                    title: 'Could not add time',
                    message: 'The quiz may have closed already. Please try again.',
                  }),
              },
            )
          }
          busy={extend.isPending}
        />

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
  const saving = createDraft.isPending || updateDraft.isPending;
  // Both buttons write the same quiz, so neither is available while the other is mid-flight.
  const busy = saving || publish.isPending;

  const patch = (index: number, next: Partial<QuestionDraft>) =>
    setQuestions((prev) => prev.map((q, i) => (i === index ? { ...q, ...next } : q)));

  /**
   * 409 and 503 mean genuinely different things and the fix differs: one is "keep teaching", the
   * other is "try again". Collapsing them into one apology would hide the only useful part.
   */
  const reportGenerationError = (error: unknown, what: 'quiz' | 'question' | 'answers') => {
    const response = (error as { response?: { status?: number; data?: { detail?: string } } })
      ?.response;
    if (response?.status === 409) {
      showToast({
        type: 'error',
        title: 'Nothing new to work from',
        // The server's wording, because it distinguishes "keep talking" from "talk about something
        // new" — two 409s that need different things from the teacher.
        message:
          response.data?.detail ??
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
  const runGenerate = async (wholeSession: boolean) => {
    setGenerating(wholeSession ? 'full' : 'quick');
    try {
      const { quiz: draft, corrections: reported } = await generate.mutateAsync({
        questionCount: wholeSession ? fullQuestionCount : questionCount,
        wholeSession,
      });
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
      setCorrections(reported);
    } catch (error) {
      reportGenerationError(error, 'quiz');
    } finally {
      setGenerating(null);
    }
  };

  /** Appends one generated question, telling the assistant what is already there so it varies. */
  const runGenerateQuestion = async () => {
    try {
      const question = await generateQuestion.mutateAsync(
        questions.map((q) => q.text).filter((text) => text.trim()),
      );
      setQuestions((prev) => [...prev, question]);
      addCorrections(question.corrections);
    } catch (error) {
      reportGenerationError(error, 'question');
    }
  };

  /** Accumulated across presses, de-duplicated: the same slip reported twice is still one slip. */
  const addCorrections = (reported: QuizCorrection[]) => {
    if (!reported?.length) return;
    setCorrections((prev) => {
      const seen = new Set(prev.map((c) => c.taught.toLowerCase()));
      return [...prev, ...reported.filter((c) => !seen.has(c.taught.toLowerCase()))];
    });
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
      addCorrections(generated.corrections);
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

      if (!publishAfter) {
        showToast({
          type: 'success',
          title: 'Draft saved',
          message: 'Nobody can see it yet. Publish when you are ready to ask the class.',
        });
        return;
      }

      await publish.mutateAsync(draft.id);
      resetComposer();
    } catch (error) {
      // 409 on the SAVE step means this quiz is no longer a draft — it has already gone to the
      // class. Telling the teacher to "try again" would just fail again, so the composer clears
      // itself instead of sitting there holding a quiz it cannot change.
      const status = (error as { response?: { status?: number } })?.response?.status;
      if (!saved && status === 409) {
        resetComposer();
        showToast({
          type: 'error',
          title: 'Already published',
          message:
            'This quiz has gone out to the class and can no longer be edited. The composer is ready for a new one.',
        });
        return;
      }

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

      {/* Two generators, because they answer two different questions a teacher asks: "did you
          follow THAT?" mid-explanation, and "what did you learn today?" at the end. They differ
          only in what the assistant reads — the recent ideas, or the whole session transcript —
          and both land in this same composer to be edited and published. Typing one by hand stays
          available below, and is the only way to make a quiz when there is no transcript at all. */}
      <div className="space-y-2 rounded-xl border border-violet-500/20 bg-violet-500/10 p-3">
        <div className="flex items-center gap-2">
          <Sparkles size={14} className="shrink-0 text-violet-300" />
          <p className="text-xs font-bold text-slate-200">Quick test</p>
        </div>
        <p className="text-[11px] text-slate-400">
          Questions on the idea you have just been explaining. Each one asks about what you have
          said since the last quiz, so pressing it again moves on rather than repeating itself.
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
            onClick={() => runGenerate(false)}
            disabled={generating !== null}
            className="mt-3 flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-violet-600 px-3 py-2 text-xs font-bold text-white transition-colors hover:bg-violet-500 disabled:opacity-60"
          >
            <Sparkles size={13} />
            {generating === 'quick' ? 'Writing questions…' : 'Quick test'}
          </button>
        </div>
        {generating === 'quick' && (
          <p className="text-[10px] text-slate-500">
            Reading back what you said and checking it against your material — this takes a few
            seconds.
          </p>
        )}
      </div>

      <div className="space-y-2 rounded-xl border border-sky-500/20 bg-sky-500/10 p-3">
        <div className="flex items-center gap-2">
          <BookOpen size={14} className="shrink-0 text-sky-300" />
          <p className="text-xs font-bold text-slate-200">Full quiz</p>
        </div>
        <p className="text-[11px] text-slate-400">
          Covers the whole lesson so far, spread across everything you have taught this session —
          including the parts a quick test has already been on.
        </p>
        <div className="flex items-center gap-2">
          <label className="text-[10px] text-slate-500">
            Questions
            <input
              type="number"
              min={1}
              max={limits.maxQuestionsPerQuiz}
              value={fullQuestionCount}
              onChange={(e) => setFullQuestionCount(Number(e.target.value))}
              className="mt-0.5 w-14 rounded border border-white/10 bg-slate-900/40 px-2 py-1 text-xs text-slate-200 outline-none"
            />
          </label>
          <button
            type="button"
            onClick={() => runGenerate(true)}
            disabled={generating !== null}
            className="mt-3 flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-sky-600 px-3 py-2 text-xs font-bold text-white transition-colors hover:bg-sky-500 disabled:opacity-60"
          >
            <BookOpen size={13} />
            {generating === 'full' ? 'Reading the lesson…' : 'Full quiz'}
          </button>
        </div>
        {generating === 'full' && (
          <p className="text-[10px] text-slate-500">
            Reading the whole session back — a longer lesson takes longer.
          </p>
        )}
      </div>

      {corrections.length > 0 && (
        <div className="space-y-2 rounded-xl border border-amber-500/30 bg-amber-500/10 p-3">
          <div className="flex items-start gap-2">
            <AlertTriangle size={14} className="mt-0.5 shrink-0 text-amber-400" />
            <div className="min-w-0">
              <p className="text-xs font-bold text-amber-200">
                Answered from your course material
              </p>
              <p className="mt-0.5 text-[11px] text-amber-200/70">
                Your material disagrees with what you said, so the answer key follows the material —
                the class should not be marked wrong for listening. Check these before publishing.
              </p>
            </div>
          </div>

          {corrections.map((correction, index) => (
            <div key={index} className="rounded-lg bg-slate-900/40 p-2 text-[11px]">
              <p className="text-slate-400">
                <span className="font-bold text-slate-500">You said: </span>
                {correction.taught}
              </p>
              <p className="mt-0.5 text-emerald-300">
                <span className="font-bold text-emerald-500/80">Material: </span>
                {correction.corrected}
              </p>
            </div>
          ))}

          <p className="text-[10px] text-amber-200/60">
            If your material is out of date, edit the answers below — the quiz is still yours.
          </p>
        </div>
      )}

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

          {/* Saving and publishing are separate on purpose. A teacher can prepare a quiz while
              still explaining and put it to the class later, and publishing stays one press —
              it saves first, so a quiz can never go out in a state the server did not accept. */}
          <div className="flex gap-2">
            <button
              type="button"
              onClick={() => save(false)}
              disabled={busy}
              className="flex flex-1 items-center justify-center gap-1.5 rounded-lg border border-white/10 px-3 py-2 text-xs font-medium text-slate-200 disabled:opacity-50"
            >
              <Save size={14} />
              {saving ? 'Saving…' : 'Save draft'}
            </button>
            <button
              type="button"
              onClick={() => save(true)}
              disabled={busy}
              className="flex flex-1 items-center justify-center gap-1.5 rounded-lg bg-violet-600 px-3 py-2 text-xs font-medium text-white disabled:opacity-50"
            >
              <Send size={14} />
              {publish.isPending ? 'Publishing…' : 'Publish to students'}
            </button>
          </div>
          <p className="text-[11px] text-slate-500">
            Publishing is final — once the class can see a quiz, its questions and answers can no
            longer be changed.
          </p>
        </>
      )}

      {/* Marks so far, live. Also what the teacher sees once a quiz has been closed. */}
      <div className="-mx-4 border-t border-white/5">
        <TeacherQuizSummary classroomId={classroomId} sessionId={sessionId} />
      </div>
    </div>
  );
};
