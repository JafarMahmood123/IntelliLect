import { 
  LiveKitRoom, 
  GridLayout, 
  ParticipantTile, 
  ControlBar, 
  RoomAudioRenderer,
  useTracks
} from "@livekit/components-react";
import { Track } from "livekit-client";
import { useEffect, useMemo, useRef } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { StatusBadge } from "../../../components/ui/StatusBadge";
import { Users } from "lucide-react";
import {
  useJoinStream,
  useLeaveStream,
  useStreamDetails,
} from "../hooks/useStreamingQueries";
import { InteractionSidebar } from "./InteractionSidebar";
import { useStreamHub } from "../hooks/useStreamHub";
import { Button } from "../../../components/ui/Button";

const toLiveKitServerUrl = (host: string): string => {
  const trimmed = host.trim();
  if (trimmed.startsWith("ws://") || trimmed.startsWith("wss://")) return trimmed;
  return `ws://${trimmed}`;
};

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
        <ControlBar variation="minimal" controls={{ chat: false, settings: false, leave: true, screenShare: true }} />
      </div>
      <RoomAudioRenderer />
    </div>
  );
};

export const LiveRoomPage = () => {
  const navigate = useNavigate();
  const { classroomId, sessionId = "" } = useParams<{ classroomId: string; sessionId: string }>();

  const { data, isPending, isError, error, refetch } = useStreamDetails(sessionId);
  const { participantCount } = useStreamHub(sessionId);
  const { mutateAsync: joinStreamAsync } = useJoinStream();
  const { mutateAsync: leaveStreamAsync } = useLeaveStream();
  
  // Use a ref to prevent double-joining/leaving in React StrictMode
  const hasJoinedApi = useRef(false);

  const serverUrl = useMemo(
    () => (data?.liveKitHost ? toLiveKitServerUrl(data.liveKitHost) : ""),
    [data?.liveKitHost]
  );

  useEffect(() => {
    if (!sessionId || !data?.joinToken || hasJoinedApi.current) return;

    const performJoin = async () => {
      try {
        hasJoinedApi.current = true;
        console.log(`[LiveRoom] API Join: ${sessionId}`);
        await joinStreamAsync(sessionId);
      } catch (err) {
        console.error("[LiveRoom] API Join Error:", err);
      }
    };

    performJoin();

    return () => {
        // Only call leave when component actually unmounts
        console.log(`[LiveRoom] API Leave: ${sessionId}`);
        leaveStreamAsync(sessionId).catch(() => {});
    };
  }, [sessionId, data?.joinToken, joinStreamAsync, leaveStreamAsync]);

  if (isPending) {
    return <div className="flex h-screen items-center justify-center bg-black text-white">Loading Session...</div>;
  }

  if (isError) {
    return (
      <div className="flex h-screen flex-col items-center justify-center bg-black text-white p-4">
        <p className="mb-4 text-red-400">{error instanceof Error ? error.message : "Stream error"}</p>
        <Button onClick={() => refetch()}>Retry</Button>
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-black">
      <header className="h-16 flex items-center justify-between px-4 border-b border-white/10 bg-slate-950 z-30 lg:px-6 pr-72">
        <div className="flex items-center gap-3">
          <h1 className="text-sm font-bold text-white">Live Classroom</h1>
          <StatusBadge status={data?.status || "Live"} />
        </div>
        <div className="flex items-center gap-2 px-3 py-1 rounded-full bg-white/5 text-white text-[10px] font-bold">
          <Users size={12} className="text-violet-400" />
          {participantCount} Online
        </div>
      </header>

      <div className="flex flex-1 overflow-hidden">
        <main className="relative flex-1 bg-slate-950">
          {data?.joinToken && serverUrl ? (
            <LiveKitRoom
              serverUrl={serverUrl}
              token={data.joinToken}
              connect={true}
              video={true} // Same for everyone
              audio={true} // Same for everyone
              onDisconnected={() => navigate(`/classrooms/${classroomId}`)}
              className="flex flex-col h-full w-full"
            >
              <VideoLayout />
            </LiveKitRoom>
          ) : (
            <div className="flex h-full items-center justify-center text-slate-500">Connecting...</div>
          )}
        </main>
        <InteractionSidebar />
      </div>
    </div>
  );
};