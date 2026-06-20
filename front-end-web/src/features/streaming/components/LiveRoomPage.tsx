import { LiveKitRoom, VideoConference } from "@livekit/components-react";
import { useCallback, useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useParams } from "react-router-dom";
import { PageHeader } from "../../../components/ui/PageHeader";
import { Button } from "../../../components/ui/Button";
import { StatusBadge } from "../../../components/ui/StatusBadge";
import { Users, ArrowLeft } from "lucide-react";
import {
  useJoinStream,
  useLeaveStream,
  useStreamDetails,
} from "../hooks/useStreamingQueries";
import { InteractionSidebar } from "./InteractionSidebar";
import { useAuthStore } from "../../../store/useAuthStore";
import { useStreamHub } from "../hooks/useStreamHub";

const toLiveKitServerUrl = (host: string): string => {
  const trimmed = host.trim();
  if (trimmed.startsWith("ws://") || trimmed.startsWith("wss://")) return trimmed;
  try {
    const withProtocol = /^https?:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
    const url = new URL(withProtocol);
    const protocol = url.protocol === "https:" ? "wss:" : "ws:";
    return `${protocol}//${url.host}${url.pathname.replace(/\/$/, "")}${url.search}`;
  } catch {
    return trimmed;
  }
};

export const LiveRoomPage = () => {
  const { t } = useTranslation("streaming");
  const navigate = useNavigate();
  const { user } = useAuthStore();
  const { classroomId, sessionId = "" } = useParams<{ classroomId: string; sessionId: string }>();

  // 1. All hooks at the top
  const { data, isPending, isError, error, refetch } = useStreamDetails(sessionId);
  const { participantCount } = useStreamHub(sessionId);
  const { mutateAsync: joinStreamAsync } = useJoinStream();
  const { mutateAsync: leaveStreamAsync } = useLeaveStream();
  
  const hasJoinedApiRef = useRef(false);
  const isTeacher = user?.roleName === "Teacher";

  const serverUrl = useMemo(
    () => (data?.liveKitHost ? toLiveKitServerUrl(data.liveKitHost) : ""),
    [data?.liveKitHost]
  );

  const handleBack = useCallback(() => {
    if (classroomId) {
      navigate(`/classrooms/${classroomId}`);
    } else {
      navigate(-1);
    }
  }, [classroomId, navigate]);

  useEffect(() => {
    if (!sessionId || !data?.joinToken) return;

    if (!hasJoinedApiRef.current) {
      hasJoinedApiRef.current = true;
      joinStreamAsync(sessionId).catch(console.error);
    }

    return () => {
      leaveStreamAsync(sessionId).catch(console.error);
    };
  }, [sessionId, data?.joinToken, joinStreamAsync, leaveStreamAsync]);

  // 2. Conditional Rendering
  if (!sessionId) return null;

  if (isPending || (!data && !isError)) {
    return (
      <div className="flex h-screen flex-col items-center justify-center bg-slate-950 text-white">
        <div className="h-10 w-10 animate-spin rounded-full border-4 border-violet-500 border-t-transparent mb-4"></div>
        <p className="text-slate-400 font-medium tracking-wide">Connecting to classroom...</p>
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex h-screen items-center justify-center bg-slate-950 p-6">
        <div className="max-w-md w-full rounded-3xl bg-slate-900 border border-white/10 p-10 text-center shadow-2xl">
          <h2 className="text-2xl font-bold text-white mb-2">Oops!</h2>
          <p className="text-slate-400 mb-8 text-sm">{error instanceof Error ? error.message : "Failed to load stream."}</p>
          <Button fullWidth onClick={() => refetch()}>Try Again</Button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-black">
      {/* 
          Header Fix: 
          1. added pr-64 (right padding) to ensure no overlap with global controls.
          2. lowered z-index slightly so global controls stay clickable on top if needed.
      */}
      <header className="h-16 flex items-center justify-between px-4 border-b border-white/10 bg-slate-900/80 backdrop-blur-md z-20 pr-64">
        <div className="flex items-center gap-4 min-w-0">
          <button 
            onClick={handleBack}
            className="flex items-center gap-2 text-slate-400 hover:text-white transition-colors"
          >
            <ArrowLeft size={20} />
          </button>
          
          <div className="min-w-0 flex items-center gap-3">
            <h1 className="text-sm font-bold text-white truncate sm:text-base">
               {data.status === 'Live' ? "🔴 Session Live" : "Session Room"}
            </h1>
            <StatusBadge status={data.status} />
          </div>
        </div>

        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2 px-3 py-1 rounded-full bg-white/5 border border-white/10 text-white text-[10px] font-bold uppercase tracking-widest">
            <div className="w-1 h-1 rounded-full bg-green-500 shadow-[0_0_8px_rgba(34,197,94,0.6)]" />
            {participantCount} Present
          </div>
        </div>
      </header>

      <div className="flex flex-1 overflow-hidden">
        {/* Main Video Area */}
        <main className="relative flex-1 bg-slate-950 overflow-hidden lk-video-container">
          {data.joinToken && serverUrl ? (
            <LiveKitRoom
              serverUrl={serverUrl}
              token={data.joinToken}
              connect={true}
              video={isTeacher}
              audio={isTeacher}
              className="flex flex-col h-full w-full"
            >
              {/* This component will now be styled correctly thanks to the @livekit/components-styles import */}
              <VideoConference />
            </LiveKitRoom>
          ) : (
            <div className="flex h-full items-center justify-center text-slate-500 text-sm">
               Securing media link...
            </div>
          )}
        </main>
        
        {/* Interaction Sidebar */}
        <InteractionSidebar />
      </div>
    </div>
  );
};