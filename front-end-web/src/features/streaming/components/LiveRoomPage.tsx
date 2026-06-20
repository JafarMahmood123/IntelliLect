import { 
  LiveKitRoom, 
  GridLayout, 
  ParticipantTile, 
  ControlBar, 
  RoomAudioRenderer,
  useTracks
} from "@livekit/components-react";
import { Track } from "livekit-client";
import { useCallback, useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useParams } from "react-router-dom";
import { StatusBadge } from "../../../components/ui/StatusBadge";
import { Users } from "lucide-react";
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

/**
 * Custom Video Layout to replace the default VideoConference
 */
const VideoLayout = () => {
  const tracks = useTracks(
    [
      { source: Track.Source.Camera, withPlaceholder: true },
      { source: Track.Source.ScreenShare, withPlaceholder: false },
    ],
    { onlySubscribed: false },
  );

  return (
    <div className="flex flex-col h-full w-full">
      <div className="flex-1 min-h-0 bg-slate-900">
        <GridLayout tracks={tracks}>
          <ParticipantTile />
        </GridLayout>
      </div>

      <div className="h-20 bg-slate-950 border-t border-white/5 flex items-center justify-center">
        <ControlBar 
          variation="minimal" 
          controls={{ 
            chat: false, 
            settings: false,
            leave: true,
            screenShare: true
          }} 
        />
      </div>

      <RoomAudioRenderer />
    </div>
  );
};

export const LiveRoomPage = () => {
  const { t } = useTranslation("streaming");
  const navigate = useNavigate();
  const { user } = useAuthStore();
  const { classroomId, sessionId = "" } = useParams<{ classroomId: string; sessionId: string }>();

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

  if (!sessionId) return null;

  if (isPending || (!data && !isError)) {
    return (
      <div className="flex h-screen flex-col items-center justify-center bg-slate-950 text-white">
        <div className="h-10 w-10 animate-spin rounded-full border-4 border-violet-500 border-t-transparent mb-4"></div>
        <p className="text-slate-400 font-medium">Entering Live Classroom...</p>
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex h-screen items-center justify-center bg-slate-950 p-6">
        <div className="max-w-md w-full rounded-2xl bg-slate-900 border border-white/10 p-10 text-center shadow-2xl">
          <p className="text-slate-400 mb-8 text-sm">{error instanceof Error ? error.message : "Failed to load stream."}</p>
          <Button fullWidth onClick={() => refetch()}>Try Again</Button>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-black">
      <header className="h-16 flex items-center justify-between px-4 border-b border-white/10 bg-slate-950 z-30 lg:px-6 pr-72">
        <div className="flex items-center gap-4 min-w-0">
          <div className="min-w-0 flex items-center gap-3">
            <h1 className="text-sm font-bold text-white truncate">
               {isTeacher ? "Teacher Mode" : "Student Mode"} | {data.status === 'Live' ? "🔴 Session Live" : "Session Room"}
            </h1>
            <StatusBadge status={data.status} />
          </div>
        </div>

        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2 px-3 py-1 rounded-full bg-white/5 border border-white/10 text-white text-[10px] font-bold uppercase tracking-widest">
            <Users size={12} className="text-violet-400" />
            {participantCount} Online
          </div>
        </div>
      </header>

      <div className="flex flex-1 overflow-hidden">
        <main className="relative flex-1 bg-slate-950 overflow-hidden">
          {data.joinToken && serverUrl ? (
            <LiveKitRoom
              serverUrl={serverUrl}
              token={data.joinToken}
              connect={true}
              video={isTeacher}
              audio={isTeacher}
              onDisconnected={() => navigate(`/classrooms/${classroomId}`)}
              className="flex flex-col h-full w-full"
            >
              <VideoLayout />
            </LiveKitRoom>
          ) : (
            <div className="flex h-full items-center justify-center text-slate-500 text-sm">
               Connecting...
            </div>
          )}
        </main>
        
        <InteractionSidebar />
      </div>
    </div>
  );
};