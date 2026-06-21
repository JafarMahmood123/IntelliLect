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

  const { data: sessions = [], isLoading } = useClassroomSessions(classroomId);
  const startSessionMutation = useStartSession(classroomId);

  const handleStartSession = async (sessionId: string) => {
    try {
      await startSessionMutation.mutateAsync(sessionId);
      showToast({
        type: "success",
        title: "Session Started",
        message: "The live stream is now active.",
      });
      navigate(`/classrooms/${classroomId}/live/${sessionId}`);
    } catch (error) {
      showToast({
        type: "error",
        title: "Launch Failed",
        message: "Could not start the session.",
      });
    }
  };

  if (isLoading) return <div className="p-8 text-center animate-pulse">Loading sessions...</div>;

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
          <p className="text-slate-500 text-sm italic">No learning sessions found for this classroom.</p>
        </div>
      ) : (
        <div className="grid gap-4">
          {sessions.map((session) => {
            const isScheduled = session.status === "Scheduled" || session.status === 0;
            const isLive = session.status === "Live" || session.status === 1;

            return (
              <div
                key={session.id}
                className={`flex items-center justify-between rounded-2xl border p-5 transition-all ${
                    isLive 
                    ? "border-red-500/20 bg-red-500/5 shadow-lg shadow-red-500/5" 
                    : "border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900/50"
                }`}
              >
                <div className="flex items-start gap-4">
                  <div
                    className={`flex h-12 w-12 items-center justify-center rounded-2xl ${
                      isLive
                        ? "bg-red-500 text-white shadow-[0_0_15px_rgba(239,68,68,0.4)] animate-pulse"
                        : "bg-slate-100 text-slate-400 dark:bg-slate-800"
                    }`}
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
                    <p className="text-xs text-slate-500 mt-1 line-clamp-1">
                      {session.description || "No description provided."}
                    </p>
                    <div className="mt-2 flex items-center gap-3 text-[10px] font-bold uppercase tracking-wider text-slate-400">
                      <span className="flex items-center gap-1.5">
                        <Clock size={12} />
                        {new Date(session.scheduledAtUtc).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}
                      </span>
                    </div>
                  </div>
                </div>

                <div className="flex items-center gap-3">
                  {isTeacher && isScheduled && (
                    <Button
                      variant="primary"
                      onClick={() => handleStartSession(session.id)}
                      isLoading={startSessionMutation.isPending && startSessionMutation.variables === session.id}
                    >
                      <PlayCircle size={18} />
                      Start Session
                    </Button>
                  )}

                  <Button
                    variant={isLive ? "danger" : "secondary"}
                    disabled={!isLive}
                    onClick={() => navigate(`/classrooms/${classroomId}/live/${session.id}`)}
                    className={`px-8 ${isLive ? 'animate-bounce shadow-lg shadow-red-500/20' : ''}`}
                  >
                    {isLive ? "Join Now" : "Locked"}
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