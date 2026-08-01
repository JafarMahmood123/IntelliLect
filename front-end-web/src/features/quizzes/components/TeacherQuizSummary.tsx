import { useMemo, useState } from 'react';
import { Trophy, Ban, Check, CheckCircle2, ChevronDown, ChevronRight, X } from 'lucide-react';
import { useSessionQuizSummary } from '../hooks/useQuizQueries';
import type { OptionBreakdown, QuestionBreakdown, StudentScore } from '../types';

interface Props {
  classroomId: string;
  sessionId: string;
}

/**
 * The teacher's marks for the whole session: who scored what, what each of them chose, and how the
 * class handled every question they were asked.
 *
 * Session-scoped rather than per-quiz because that is the question a teacher actually asks. Nothing
 * here depends on the session being live, so the same component serves the in-session view and the
 * one opened after the session has ended.
 */
export const TeacherQuizSummary = ({ classroomId, sessionId }: Props) => {
  const { data, isPending, isError } = useSessionQuizSummary(classroomId, sessionId);

  // The per-student answers travel as ids, so the text is looked up here rather than repeated for
  // every student in the payload.
  const questionsById = useMemo(
    () => new Map((data?.questions ?? []).map((q) => [q.questionId, q])),
    [data],
  );

  // Grouped so the teacher sees "the quizzes I published", not one undifferentiated list of
  // questions from several of them.
  const quizzes = useMemo(() => groupByQuiz(data?.questions ?? []), [data]);

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

      {/* --- who scored what, and what they chose --- */}
      <div className="space-y-1.5">
        {data.students.length === 0 ? (
          <p className="text-[11px] text-slate-500">Nobody has taken part yet.</p>
        ) : (
          data.students.map((student, index) => (
            <StudentRow
              key={student.studentId}
              student={student}
              rank={index}
              questionsById={questionsById}
            />
          ))
        )}
      </div>

      {/* --- the quizzes themselves --- */}
      <div>
        <h4 className="text-xs font-bold text-slate-300">Quizzes you published</h4>
        <p className="mt-0.5 text-[11px] text-slate-500">
          A wrong answer taking most of the votes usually means a misconception worth revisiting.
        </p>
      </div>

      <div className="space-y-3">
        {quizzes.map((quiz) => (
          <div key={quiz.quizId}>
            <div className="mb-1.5 flex items-baseline justify-between gap-2">
              <p className="min-w-0 truncate text-[11px] font-bold text-slate-300">{quiz.title}</p>
              <span
                className={`shrink-0 text-[10px] ${
                  quiz.countsTowardsMarks ? 'text-slate-500' : 'text-amber-400'
                }`}
              >
                {quiz.countsTowardsMarks ? quiz.status : 'Cancelled'}
              </span>
            </div>

            <div className="space-y-2">
              {quiz.questions.map((question, index) => (
                <QuestionCard key={question.questionId} question={question} index={index} />
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

/**
 * One student, expandable into every choice they made.
 *
 * Collapsed by default because the ranked list is what a teacher reads first; the individual
 * answers are what they open when a name in it surprises them.
 */
const StudentRow = ({
  student,
  rank,
  questionsById,
}: {
  student: StudentScore;
  rank: number;
  questionsById: Map<string, QuestionBreakdown>;
}) => {
  const [open, setOpen] = useState(false);
  const hasAnswers = student.answers.length > 0;

  return (
    <div className="rounded-xl border border-white/5 bg-white/5">
      <button
        type="button"
        disabled={!hasAnswers}
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-3 p-2.5 text-left disabled:cursor-default"
      >
        <span className="w-5 shrink-0 text-center text-[11px] font-bold text-slate-500">
          {rank === 0 ? <Trophy size={13} className="mx-auto text-amber-400" /> : rank + 1}
        </span>
        <span className="flex min-w-0 flex-1 items-center gap-1">
          {hasAnswers &&
            (open ? (
              <ChevronDown size={12} className="shrink-0 text-slate-500" />
            ) : (
              <ChevronRight size={12} className="shrink-0 text-slate-500" />
            ))}
          <span className="min-w-0 truncate text-xs font-medium text-slate-200">
            {student.studentName}
          </span>
        </span>
        <span className="shrink-0 text-[11px] text-slate-500">
          {hasAnswers ? `${student.correctCount}/${student.answeredCount} correct` : 'No answers'}
        </span>
        <span className="shrink-0 text-xs font-bold text-slate-100">
          {student.score}
          <span className="text-slate-500">/{student.totalPointsAvailable}</span>
        </span>
        <span className="w-9 shrink-0 text-right text-[11px] font-bold text-violet-400">
          {student.percentage}%
        </span>
      </button>

      {open && (
        <div className="space-y-1.5 border-t border-white/5 p-2.5">
          {student.answers.map((answer) => {
            const question = questionsById.get(answer.questionId);
            const chosen = question?.options.find((o) => o.optionId === answer.selectedOptionId);
            const correct = question?.options.find((o) => o.isCorrect);

            return (
              <div key={answer.questionId} className="rounded-lg bg-slate-900/40 p-2">
                <p className="text-[11px] leading-snug text-slate-300">
                  {question?.text ?? 'Question'}
                </p>
                <p
                  className={`mt-1 flex items-start gap-1.5 text-[11px] ${
                    answer.isCorrect ? 'text-emerald-400' : 'text-red-400'
                  }`}
                >
                  {answer.isCorrect ? (
                    <Check size={12} className="mt-0.5 shrink-0" />
                  ) : (
                    <X size={12} className="mt-0.5 shrink-0" />
                  )}
                  <span className="min-w-0">{chosen?.text ?? 'Answer'}</span>
                </p>
                {/* Only when they got it wrong — repeating the right answer under a correct one
                    is noise, and it is the mistakes a teacher is scanning for. */}
                {!answer.isCorrect && correct && (
                  <p className="mt-0.5 flex items-start gap-1.5 text-[10px] text-slate-500">
                    <CheckCircle2 size={10} className="mt-0.5 shrink-0 text-emerald-500/60" />
                    <span className="min-w-0">{correct.text}</span>
                  </p>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
};

const QuestionCard = ({ question, index }: { question: QuestionBreakdown; index: number }) => (
  <div
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
      {question.options.map((option) => (
        <OptionBar key={option.optionId} option={option} answeredCount={question.answeredCount} />
      ))}
    </div>
  </div>
);

const OptionBar = ({
  option,
  answeredCount,
}: {
  option: OptionBreakdown;
  answeredCount: number;
}) => {
  const share = answeredCount > 0 ? Math.round((option.selectedCount / answeredCount) * 100) : 0;

  return (
    <div className="flex items-center gap-2 text-[11px]">
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
          className={`block h-full ${option.isCorrect ? 'bg-emerald-500' : 'bg-slate-600'}`}
          style={{ width: `${share}%` }}
        />
      </span>
      <span className="w-6 shrink-0 text-right text-slate-500">{option.selectedCount}</span>
    </div>
  );
};

/**
 * Questions arrive flat, carrying their quiz's title and status on every row. Grouped in first-seen
 * order, which is the server's oldest-first quiz ordering.
 */
const groupByQuiz = (questions: QuestionBreakdown[]) => {
  const quizzes: {
    quizId: string;
    title: string;
    status: string;
    countsTowardsMarks: boolean;
    questions: QuestionBreakdown[];
  }[] = [];

  for (const question of questions) {
    let quiz = quizzes.find((q) => q.quizId === question.quizId);
    if (!quiz) {
      quiz = {
        quizId: question.quizId,
        title: question.quizTitle || 'Quiz',
        status: question.quizStatus,
        countsTowardsMarks: question.countsTowardsMarks,
        questions: [],
      };
      quizzes.push(quiz);
    }
    quiz.questions.push(question);
  }

  return quizzes;
};
