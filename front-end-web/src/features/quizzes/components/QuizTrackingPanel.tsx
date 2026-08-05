import { BarChart3, CalendarDays, ListChecks, Trophy, Users } from 'lucide-react';
import {
  useClassroomQuizTracking,
  useMyClassroomQuizTracking,
} from '../hooks/useQuizQueries';
import type { MyClassroomQuizTracking } from '../types';

interface Props {
  classroomId: string;
  isTeacher: boolean;
}

/**
 * Cumulative progress across the whole classroom.
 *
 * The session summary answers "how did today go". This answers the question that only shows up
 * over weeks: who is falling behind, which lesson went worst, how much of what was offered each
 * student actually sat.
 */
export const QuizTrackingPanel = ({ classroomId, isTeacher }: Props) =>
  isTeacher ? (
    <TeacherTracking classroomId={classroomId} />
  ) : (
    <StudentTracking classroomId={classroomId} />
  );

const TeacherTracking = ({ classroomId }: { classroomId: string }) => {
  const { data, isPending, isError } = useClassroomQuizTracking(classroomId);

  if (isPending) return <Loading />;
  if (isError) return <Failed />;
  if (data.quizCount === 0) return <NothingYet isTeacher />;

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Stat icon={<Users size={16} />} label="Students" value={`${data.activeStudentCount}`}
          hint={`${data.enrolledStudentCount} enrolled`} />
        <Stat icon={<ListChecks size={16} />} label="Quizzes" value={`${data.quizCount}`}
          hint={`${data.totalPointsAvailable} marks`} />
        <Stat icon={<CalendarDays size={16} />} label="Sessions" value={`${data.sessionsWithQuizzesCount}`}
          hint={`of ${data.sessionCount} run`} />
        <Stat icon={<BarChart3 size={16} />} label="Class average"
          value={`${data.classAveragePercentage}%`} hint="of those taking part" />
      </div>

      <Section
        title="Cumulative scores"
        hint="Measured against every mark offered to the class, not only the quizzes each student sat."
      >
        <div className="space-y-1.5">
          {data.students.map((student, index) => (
            <div
              key={student.studentId}
              className="flex items-center gap-3 rounded-xl border border-slate-200 bg-white p-3 dark:border-slate-800 dark:bg-slate-900/50"
            >
              <span className="w-5 shrink-0 text-center text-xs font-bold text-slate-400">
                {index === 0 ? <Trophy size={14} className="mx-auto text-amber-500" /> : index + 1}
              </span>
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-bold text-slate-900 dark:text-white">
                  {student.studentName}
                </p>
                <p className="mt-0.5 text-xs text-slate-500">
                  {student.quizzesTaken} of {student.quizCount} quizzes ·{' '}
                  {student.sessionsTakenPart} of {student.sessionsWithQuizzesCount} sessions ·{' '}
                  {student.correctCount}/{student.answeredCount} correct
                </p>
              </div>
              <Score value={student.score} total={student.totalPointsAvailable}
                percentage={student.percentage} />
            </div>
          ))}
        </div>
      </Section>

      <Section title="Sessions" hint="Newest first. A low average is a lesson worth revisiting.">
        <div className="space-y-1.5">
          {data.sessions.map((session) => (
            <div
              key={session.sessionId}
              className="flex items-center gap-3 rounded-xl border border-slate-200 bg-white p-3 dark:border-slate-800 dark:bg-slate-900/50"
            >
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-bold text-slate-900 dark:text-white">
                  {session.title}
                </p>
                <p className="mt-0.5 text-xs text-slate-500">
                  {new Date(session.scheduledAtUtc).toLocaleDateString([], { dateStyle: 'medium' })}{' '}
                  · {session.quizCount} quiz(zes) · {session.participantCount} took part
                </p>
              </div>
              <Percentage value={session.averagePercentage} />
            </div>
          ))}
        </div>
      </Section>
    </div>
  );
};

const StudentTracking = ({ classroomId }: { classroomId: string }) => {
  const { data, isPending, isError } = useMyClassroomQuizTracking(classroomId);

  if (isPending) return <Loading />;
  if (isError) return <Failed />;
  if (data.quizCount === 0) return <NothingYet isTeacher={false} />;

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Stat icon={<BarChart3 size={16} />} label="Your total"
          value={`${data.score}/${data.totalPointsAvailable}`} hint={`${data.percentage}%`} />
        <Stat icon={<Users size={16} />} label="Class average"
          value={`${data.classAveragePercentage}%`} hint={comparison(data)} />
        <Stat icon={<ListChecks size={16} />} label="Quizzes taken"
          value={`${data.quizzesTaken}`} hint={`of ${data.quizCount}`} />
        <Stat icon={<CalendarDays size={16} />} label="Sessions"
          value={`${data.sessionsTakenPart}`} hint={`of ${data.sessionsWithQuizzesCount}`} />
      </div>

      <Section
        title="Session by session"
        hint="A session you missed still counts against the total — that is why it is listed."
      >
        <div className="space-y-1.5">
          {data.sessions.map((session) => (
            <div
              key={session.sessionId}
              className={`flex items-center gap-3 rounded-xl border p-3 ${
                session.quizzesTaken === 0
                  ? 'border-amber-300 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/5'
                  : 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900/50'
              }`}
            >
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-bold text-slate-900 dark:text-white">
                  {session.title}
                </p>
                <p className="mt-0.5 text-xs text-slate-500">
                  {new Date(session.scheduledAtUtc).toLocaleDateString([], { dateStyle: 'medium' })}
                  {session.quizzesTaken === 0
                    ? ' · you did not take this one'
                    : ` · ${session.quizzesTaken} of ${session.quizCount} quiz(zes)`}
                </p>
              </div>
              <Score value={session.score} total={session.totalPoints}
                percentage={session.percentage} />
            </div>
          ))}
        </div>
      </Section>
    </div>
  );
};

/** Plain language rather than a bare delta — "you are 12 points above" reads as a mark, not a gap. */
const comparison = (data: MyClassroomQuizTracking): string => {
  const gap = data.percentage - data.classAveragePercentage;
  if (gap === 0) return 'exactly the average';
  return gap > 0 ? `you are ${gap}% above` : `you are ${Math.abs(gap)}% below`;
};

const Stat = ({
  icon,
  label,
  value,
  hint,
}: {
  icon: React.ReactNode;
  label: string;
  value: string;
  hint: string;
}) => (
  <div className="rounded-xl border border-slate-200 bg-white p-3 dark:border-slate-800 dark:bg-slate-900/50">
    <p className="flex items-center gap-1.5 text-xs font-medium text-slate-500">
      <span className="text-violet-500">{icon}</span>
      {label}
    </p>
    <p className="mt-1 text-xl font-bold text-slate-900 dark:text-white">{value}</p>
    <p className="text-xs text-slate-500">{hint}</p>
  </div>
);

const Score = ({
  value,
  total,
  percentage,
}: {
  value: number;
  total: number;
  percentage: number;
}) => (
  <div className="shrink-0 text-right">
    <p className="text-sm font-bold text-slate-900 dark:text-white">
      {value}
      <span className="text-slate-400">/{total}</span>
    </p>
    <Percentage value={percentage} />
  </div>
);

/** Colour is a hint, never the only signal — the number is always there beside it. */
const Percentage = ({ value }: { value: number }) => (
  <p
    className={`text-xs font-bold ${
      value >= 70 ? 'text-emerald-500' : value >= 40 ? 'text-amber-500' : 'text-red-500'
    }`}
  >
    {value}%
  </p>
);

const Section = ({
  title,
  hint,
  children,
}: {
  title: string;
  hint: string;
  children: React.ReactNode;
}) => (
  <div>
    <h3 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h3>
    <p className="mb-2 mt-0.5 text-xs text-slate-500">{hint}</p>
    {children}
  </div>
);

const Loading = () => (
  <div className="p-8 text-center text-sm text-slate-500">Loading progress…</div>
);

const Failed = () => (
  <div className="p-8 text-center text-sm text-red-500">Could not load progress.</div>
);

const NothingYet = ({ isTeacher }: { isTeacher: boolean }) => (
  <div className="py-12 text-center">
    <BarChart3 className="mx-auto mb-4 text-slate-300" size={48} />
    <p className="text-sm font-medium text-slate-600 dark:text-slate-300">No quizzes yet</p>
    <p className="mt-1 text-xs text-slate-500">
      {isTeacher
        ? 'Progress appears here once you have run a quiz in a session.'
        : // Not "once your teacher has run a quiz": a quiz that is still open is deliberately
          // absent from these totals, so this has to be true while one is in progress too.
          'Your marks appear here once a quiz has been closed.'}
    </p>
  </div>
);
