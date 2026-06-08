import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Calendar, Video, Clock, Plus, PlayCircle } from "lucide-react";
import { Button } from "../../../components/ui/Button";
import { StatusBadge } from "../../../components/ui/StatusBadge";
import {
  useClassroomSessions,
  useStartSession,
} from "../hooks/useClassroomQueries";
import { CreateSessionDrawer } from "./CreateSessionDrawer";
import { useToast } from "../../../components/ui/ToastProvider";

interface ClassroomSessionListProps {
  classroomId: string;
  isTeacher: boolean;
}

export const ClassroomSessionList = ({
  classroomId,
  isTeacher,
}: ClassroomSessionListProps) => {
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);
  const navigate = useNavigate();
  const { showToast } = useToast();

  // Fetch sessions for the specific classroom
  const { data: sessions = [], isLoading } = useClassroomSessions(classroomId);

  // Mutation to transition session status from Scheduled to Live
  const startSessionMutation = useStartSession(classroomId);

  const handleStartSession = async (sessionId: string) => {
    try {
      await startSessionMutation.mutateAsync(sessionId);
      showToast({
        type: "success",
        title: "Session Started",
        message: "The live stream is now active.",
      });
      // Navigate to the live streaming interface
      navigate(`/classrooms/${classroomId}/live/${sessionId}`);
    } catch (error) {
      showToast({
        type: "error",
        title: "Launch Failed",
        message: "Could not start the session. Please try again.",
      });
    }
  };

  if (isLoading)
    return (
      <div className="p-8 text-center animate-pulse">Loading sessions...</div>
    );

  return (
    <div className="space-y-6">
      {isTeacher && (
        <div className="flex justify-end">
          <Button onClick={() => setIsDrawerOpen(true)}>
            <Plus size={18} />
            Schedule Session
          </Button>
        </div>
      )}

      {sessions.length === 0 ? (
        <div className="py-12 text-center">
          <Calendar className="mx-auto mb-4 text-slate-300" size={48} />
          <p className="text-slate-500">No sessions scheduled yet.</p>
        </div>
      ) : (
        <div className="grid gap-4">
          {sessions.map((session) => {
            // Fix: Handle both string and numeric statuses from backend enums
            // Backend: 0 = Scheduled, 1 = Live, 2 = Ended
            const isScheduled =
              session.status === "Scheduled" || session.status === 0;
            const isLive = session.status === "Live" || session.status === 1;

            return (
              <div
                key={session.id}
                className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50/50 p-5 dark:border-slate-800 dark:bg-slate-900/50 transition-all hover:bg-slate-100/50 dark:hover:bg-slate-800/80"
              >
                <div className="flex items-start gap-4">
                  <div
                    className={`flex h-12 w-12 items-center justify-center rounded-full ${
                      isLive
                        ? "bg-red-100 text-red-600 animate-pulse"
                        : "bg-indigo-100 text-indigo-600"
                    } dark:bg-opacity-20`}
                  >
                    <Video size={24} />
                  </div>
                  <div>
                    <div className="flex items-center gap-3">
                      <h4 className="font-bold text-slate-900 dark:text-white">
                        {session.title}
                      </h4>
                      <StatusBadge status={session.status} />
                    </div>
                    <p className="text-sm text-slate-500 line-clamp-1">
                      {session.description}
                    </p>
                    <div className="mt-2 flex items-center gap-3 text-xs font-medium text-slate-600 dark:text-slate-400">
                      <span className="flex items-center gap-1">
                        <Clock size={14} />
                        {new Date(session.scheduledAtUtc).toLocaleString()}
                      </span>
                    </div>
                  </div>
                </div>

                <div className="flex items-center gap-2">
                  {/* Teacher-only control to start a scheduled session */}
                  {isTeacher && isScheduled && (
                    <Button
                      variant="primary"
                      onClick={() => handleStartSession(session.id)}
                      isLoading={
                        startSessionMutation.isPending &&
                        startSessionMutation.variables === session.id
                      }
                    >
                      <PlayCircle size={18} />
                      Start Now
                    </Button>
                  )}

                  {/* Join button: Only interactive if the session is currently Live */}
                  <Button
                    variant={isLive ? "primary" : "secondary"}
                    disabled={!isLive}
                    onClick={() =>
                      navigate(`/classrooms/${classroomId}/live/${session.id}`)
                    }
                    className="px-6"
                  >
                    {isLive ? "Join Now" : "Join"}
                  </Button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      <CreateSessionDrawer
        isOpen={isDrawerOpen}
        onClose={() => setIsDrawerOpen(false)}
        classroomId={classroomId}
      />
    </div>
  );
};
