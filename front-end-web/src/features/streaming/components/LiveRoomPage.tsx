import {
  LiveKitRoom,
  ControlBar,
  RoomAudioRenderer,
  ConnectionStateToast,
} from "@livekit/components-react";
import type { DisconnectReason } from "livekit-client";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { StatusBadge } from "../../../components/ui/StatusBadge";
import { Users, StopCircle } from "lucide-react";
import {
  useJoinStream,
  useLeaveStream,
  useStreamDetails,
} from "../hooks/useStreamingQueries";
import { InteractionSidebar } from "./InteractionSidebar";
import { TeacherFeedbackPanel } from "./TeacherFeedbackPanel";
import { useStreamHub } from "../hooks/useStreamHub";
import { Button } from "../../../components/ui/Button";
import { ConfirmationModal } from "../../../components/ui/ConfirmationModal";
import { useToast } from "../../../components/ui/ToastProvider";
import { useAuthStore } from "../../../store/useAuthStore";
import { useEndSession } from "../../classrooms/hooks/useClassroomQueries";
import {
  describeSessionEnd,
  describeSessionEndError,
} from "../../classrooms/utils/sessionEnd";
import { TeacherStage } from "./stage/TeacherStage";
import { StudentStage } from "./stage/StudentStage";
import { SpeakerPicker } from "./SpeakerPicker";
import { toRoomConnectOptions, toRoomOptions } from "../config/toRoomOptions";
import { shouldExitSession } from "../utils/disconnectPolicy";

const toLiveKitServerUrl = (host: string): string => {
  const trimmed = host.trim();
  if (trimmed.startsWith("ws://") || trimmed.startsWith("wss://")) return trimmed;
  return `ws://${trimmed}`;
};

/**
 * Base live-session screen. Owns the shared shell — LiveKit connection, header, participant
 * count, the control bar, room audio and the interaction sidebar — and delegates the video area
 * to a role-specific stage (<TeacherStage> / <StudentStage>) so each can be evolved on its own.
 */
export const LiveRoomPage = () => {
  const navigate = useNavigate();
  const { user } = useAuthStore();
  const { classroomId, sessionId = "" } = useParams<{ classroomId: string; sessionId: string }>();

  const { showToast } = useToast();

  const { data, isPending, isError, error, refetch } = useStreamDetails(sessionId);
  const { participantCount, hasEnded, publishPolicy, recordingState } = useStreamHub(sessionId);
  const { mutateAsync: joinStreamAsync } = useJoinStream();
  const { mutateAsync: leaveStreamAsync } = useLeaveStream();
  const endSessionMutation = useEndSession(classroomId ?? "");

  const hasJoinedApi = useRef(false);
  const hasLeftRoom = useRef(false);
  const [isConfirmingEnd, setIsConfirmingEnd] = useState(false);
  // The media transport dropped and the SDK exhausted its retries, but the session itself is still
  // running. We stay on the page and offer a rejoin instead of ejecting the user (see
  // disconnectPolicy). This UNMOUNTS <LiveKitRoom>, so the room is fully torn down and a rejoin
  // constructs a fresh one — construction options are frozen per room, so reusing it would keep the
  // old settings.
  const [connectionLost, setConnectionLost] = useState(false);

  const isTeacher = user?.roleName === "Teacher";

  // Single exit path, so leaving is idempotent: the teacher's own end, the "session ended"
  // broadcast and LiveKit's disconnect can all fire for the same session end.
  const exitToClassroom = useCallback(() => {
    if (hasLeftRoom.current) return;
    hasLeftRoom.current = true;
    navigate(`/classrooms/${classroomId}`);
  }, [classroomId, navigate]);

  // The server announced the session is over. Everyone is told why and sent back to the
  // classroom; the media room is closed right behind this, so lingering here is not an option.
  useEffect(() => {
    if (!hasEnded || hasLeftRoom.current) return;

    showToast({
      type: "info",
      title: "Session Ended",
      // The teacher already saw the result of their own action.
      message: isTeacher
        ? "This session has been closed."
        : "The teacher has ended this session. The summary will appear in your classroom shortly.",
    });
    exitToClassroom();
  }, [hasEnded, isTeacher, showToast, exitToClassroom]);

  const handleEndSession = async () => {
    if (!sessionId) return;

    try {
      const outcome = await endSessionMutation.mutateAsync(sessionId);
      showToast(describeSessionEnd(outcome));
      setIsConfirmingEnd(false);
      exitToClassroom();
    } catch (err) {
      setIsConfirmingEnd(false);
      showToast({
        type: "error",
        title: "Could Not End Session",
        message: describeSessionEndError(err),
      });
    }
  };

  // Whether STUDENTS may publish each source, as the teacher has it set right now: a live
  // SignalR update (Session Settings toggle) wins; otherwise the initial value from the stream
  // details. The teacher themselves always publishes freely.
  const studentsCanAudio = publishPolicy?.canPublishAudio ?? data?.studentsCanPublishAudio ?? false;
  const studentsCanVideo = publishPolicy?.canPublishVideo ?? data?.studentsCanPublishVideo ?? false;
  const canPublishAudio = isTeacher || studentsCanAudio;
  const canPublishVideo = isTeacher || studentsCanVideo;

  // The device request handed to <LiveKitRoom> is frozen at connect time: it decides what gets
  // published when this participant joins. We deliberately do NOT re-drive it from the live policy
  // — re-granting video should let a student turn their camera ON, never force it on for them.
  // Live changes only flow to the ControlBar (which shows/hides the buttons); revocation is already
  // enforced server-side by the media server force-unpublishing the track.
  const initialPublishRef = useRef<{ audio: boolean; video: boolean } | null>(null);
  if (initialPublishRef.current === null && data) {
    initialPublishRef.current = {
      audio: isTeacher || (data.studentsCanPublishAudio ?? false),
      video: isTeacher || (data.studentsCanPublishVideo ?? false),
    };
  }
  const initialAudio = initialPublishRef.current?.audio ?? false;
  const initialVideo = initialPublishRef.current?.video ?? false;

  // Recording state as it stands right now: a live SignalR update wins; otherwise the value
  // carried in the initial stream details, so someone joining mid-recording sees the indicator
  // immediately instead of only after the next change.
  const currentRecordingState = recordingState ?? data?.recordingState ?? "Off";
  const isRecording = currentRecordingState === "Recording";

  // Tell everyone when recording starts or stops. Unlike the publish-policy toast below this also
  // fires for the teacher: they get the confirmation that their toggle actually took effect
  // server-side, rather than trusting the button.
  const prevRecordingRef = useRef<typeof recordingState>(null);
  useEffect(() => {
    if (!recordingState) return;
    const prev = prevRecordingRef.current;
    prevRecordingRef.current = recordingState;
    if (prev === null || prev === recordingState) return;

    if (recordingState === "Recording") {
      showToast({
        type: "info",
        title: "Recording started",
        message: "This session is now being recorded.",
      });
    } else {
      showToast({
        type: "info",
        title: "Recording stopped",
        message: "This session is no longer being recorded.",
      });
    }
  }, [recordingState, showToast]);

  // Tell a student when the teacher changes what they may share (fires only on a real SignalR
  // update, never on the initial load — publishPolicy is null until the first change arrives).
  const prevPolicyRef = useRef<typeof publishPolicy>(null);
  useEffect(() => {
    if (isTeacher || !publishPolicy) {
      prevPolicyRef.current = publishPolicy;
      return;
    }
    const prev = prevPolicyRef.current;
    prevPolicyRef.current = publishPolicy;
    if (
      prev &&
      prev.canPublishAudio === publishPolicy.canPublishAudio &&
      prev.canPublishVideo === publishPolicy.canPublishVideo
    ) {
      return; // no actual change
    }
    showToast({
      type: "info",
      title: "Sharing permissions updated",
      message: `The teacher ${publishPolicy.canPublishVideo ? "enabled" : "disabled"} camera and ${
        publishPolicy.canPublishAudio ? "enabled" : "disabled"
      } microphone sharing for students.`,
    });
  }, [publishPolicy, isTeacher, showToast]);

  const serverUrl = useMemo(
    () => (data?.liveKitHost ? toLiveKitServerUrl(data.liveKitHost) : ""),
    [data?.liveKitHost],
  );

  // Server-owned media quality + reconnection settings (StreamingService "Media" appsettings). The
  // mappers validate every value and fall back to bundled defaults, so an absent or misconfigured
  // `media` object still yields a working room. Memoized because these are Room CONSTRUCTION
  // options — handing <LiveKitRoom> a fresh object each render would churn the room.
  const roomOptions = useMemo(() => toRoomOptions(data?.media), [data?.media]);
  const roomConnectOptions = useMemo(() => toRoomConnectOptions(data?.media), [data?.media]);

  // A disconnect only ends the session when it is genuinely terminal (the user left, the room was
  // closed, they were evicted) or the server already announced the end. A transport failure keeps
  // them here with a rejoin option — previously ANY disconnect navigated away, so one blip ejected
  // a student from a live lecture.
  const handleDisconnected = useCallback(
    (reason?: DisconnectReason) => {
      if (shouldExitSession(reason, hasEnded)) {
        exitToClassroom();
        return;
      }
      setConnectionLost(true);
      showToast({
        type: "error",
        title: "Connection Lost",
        message: "You were disconnected from the live session. The session is still running.",
      });
    },
    [hasEnded, exitToClassroom, showToast],
  );

  const handleRejoin = useCallback(() => {
    setConnectionLost(false);
    // Re-fetch so a stale join token is not reused for the reconnect attempt.
    void refetch();
  }, [refetch]);

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
      console.log(`[LiveRoom] API Leave: ${sessionId}`);
      leaveStreamAsync(sessionId).catch(() => {});
    };
  }, [sessionId, data?.joinToken, joinStreamAsync, leaveStreamAsync]);

  if (isPending) {
    return (
      <div className="flex h-screen items-center justify-center bg-black text-white font-medium animate-pulse">
        Loading Session...
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex h-screen flex-col items-center justify-center bg-black text-white p-4 text-center">
        <p className="mb-4 text-red-400 font-semibold">
          {error instanceof Error ? error.message : "Stream connection error"}
        </p>
        <Button onClick={() => refetch()}>Retry Connection</Button>
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-black">
      <header className="h-16 flex items-center justify-between px-4 border-b border-white/10 bg-slate-950 z-30 lg:px-6">
        <div className="flex items-center gap-3">
          <h1 className="text-sm font-bold text-white">Live Classroom</h1>
          <StatusBadge status={data?.status || "Live"} />
          {/* Visible to EVERYONE, not just the teacher: people are entitled to know when they
              are being recorded. */}
          {isRecording && (
            <div
              className="flex items-center gap-1.5 px-2.5 py-1 rounded-full bg-red-500/15 text-red-400 text-[10px] font-bold"
              role="status"
            >
              <span className="h-2 w-2 rounded-full bg-red-500 animate-pulse" />
              REC
            </div>
          )}
        </div>
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2 px-3 py-1 rounded-full bg-white/5 text-white text-[10px] font-bold">
            <Users size={12} className="text-violet-400" />
            {participantCount} Online
          </div>

          {/* Ending the session is the teacher's alone, and it is deliberately distinct from
              leaving the room: leaving keeps the session live for the students. */}
          {isTeacher && (
            <Button
              variant="danger"
              onClick={() => setIsConfirmingEnd(true)}
              isLoading={endSessionMutation.isPending}
              className="!py-1.5 !px-3 text-xs"
            >
              <StopCircle size={14} />
              End Session
            </Button>
          )}
        </div>
      </header>

      <div className="flex flex-1 overflow-hidden">
        <main className="relative flex-1 bg-slate-950">
          {data?.joinToken && serverUrl && !connectionLost ? (
            <LiveKitRoom
              serverUrl={serverUrl}
              token={data.joinToken}
              connect={true}
              // Server-owned quality settings + reconnection budget. Without these the room ran on
              // livekit-client defaults, which had adaptiveStream/dynacast off and maxRetries at 1.
              options={roomOptions}
              connectOptions={roomConnectOptions}
              // Initial device request, frozen at connect time (see initialPublishRef). Live
              // policy changes flow only to the ControlBar below, never back into this — so a
              // re-grant lets a student choose to share rather than force-enabling their camera.
              video={initialVideo}
              audio={initialAudio}
              // Terminal disconnects exit; a transport failure keeps the user here with a rejoin
              // option instead of ejecting them from a running lecture.
              onDisconnected={handleDisconnected}
              className="flex flex-col h-full w-full"
            >
              <div className="flex-1 min-h-0 bg-slate-900">
                {isTeacher ? <TeacherStage /> : <StudentStage />}
              </div>

              <div className="h-20 bg-slate-950 border-t border-white/5 flex items-center justify-center">
                <ControlBar
                  variation="minimal"
                  controls={{
                    chat: false,
                    settings: false,
                    leave: true,
                    camera: canPublishVideo,
                    microphone: canPublishAudio,
                    screenShare: isTeacher,
                  }}
                />
                {/* ControlBar has no speaker picker of its own — it only ever renders audioinput
                    and videoinput menus — so this sits alongside it rather than inside it. */}
                <SpeakerPicker />
              </div>

              <RoomAudioRenderer />

              {/* Makes the SDK's own reconnect attempts visible. With MaxRetries now at 5 that
                  window can last several seconds, and silence there reads as a frozen app. */}
              <ConnectionStateToast />

              {/* Private, teacher-only live-feedback panel. Subscribes to the EXISTING room's
                  data channel — never a new connection. */}
              {isTeacher && <TeacherFeedbackPanel />}
            </LiveKitRoom>
          ) : connectionLost ? (
            /* The transport failed and the SDK gave up, but the session is still live. Keeping the
               user here with an explicit rejoin beats navigating them out of a running lecture. */
            <div className="flex h-full flex-col items-center justify-center gap-4 px-6 text-center">
              <p className="text-slate-300 font-medium">You were disconnected</p>
              <p className="text-slate-500 text-sm max-w-md">
                The session is still running. Check your connection and rejoin.
              </p>
              <Button onClick={handleRejoin}>Rejoin session</Button>
            </div>
          ) : (
            <div className="flex h-full items-center justify-center text-slate-500">
              Connecting to media server...
            </div>
          )}
        </main>
        <InteractionSidebar />
      </div>

      <ConfirmationModal
        isOpen={isConfirmingEnd}
        onClose={() => setIsConfirmingEnd(false)}
        onConfirm={handleEndSession}
        title="End this session for everyone?"
        description="All students will be disconnected immediately. The recording will be finalized and the session summary and notes generated. This cannot be undone."
        confirmText="End Session"
        variant="danger"
        isLoading={endSessionMutation.isPending}
      />
    </div>
  );
};
