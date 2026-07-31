import { useState } from 'react';
import { Mic, Video, Circle } from 'lucide-react';
import {
  useStreamDetails,
  useUpdatePublishPolicy,
  useUpdateRecording,
} from '../hooks/useStreamingQueries';
import { useToast } from '../../../components/ui/ToastProvider';
import type { PublishPolicy, RecordingState } from '../types';

interface SessionSettingsPanelProps {
  sessionId: string;
  /** Latest policy pushed over SignalR (null until the first change this session). */
  livePolicy: PublishPolicy | null;
  /** Latest recording state pushed over SignalR (null until the first change this session). */
  liveRecordingState: RecordingState | null;
}

/**
 * Teacher-only in-session controls. Lets the teacher toggle, in real time, whether students may
 * share their camera and microphone. Each toggle calls the backend, which enforces the change on
 * already-connected students (force-stopping a now-forbidden track) and broadcasts it so every
 * client updates immediately.
 */
export const SessionSettingsPanel = ({
  sessionId,
  livePolicy,
  liveRecordingState,
}: SessionSettingsPanelProps) => {
  const { data } = useStreamDetails(sessionId);
  const { showToast } = useToast();
  const { mutate, isPending, variables } = useUpdatePublishPolicy(sessionId);

  // Current policy: a live SignalR update wins; otherwise the value from the initial stream
  // details. While a toggle is in flight, show its target for a snappy, optimistic UI.
  const current: PublishPolicy = livePolicy ?? {
    canPublishAudio: data?.studentsCanPublishAudio ?? false,
    canPublishVideo: data?.studentsCanPublishVideo ?? false,
  };
  const shown = isPending && variables ? variables : current;

  const apply = (next: PublishPolicy) => {
    mutate(next, {
      onError: () =>
        showToast({
          type: 'error',
          title: 'Could not update settings',
          message: 'The change did not go through. Please try again.',
        }),
    });
  };

  return (
    <div className="p-4 space-y-4">
      <RecordingControl sessionId={sessionId} liveState={liveRecordingState} />

      <div>
        <h3 className="text-sm font-bold text-slate-200">Student permissions</h3>
        <p className="text-[11px] text-slate-500 mt-0.5">
          Control what students can share. Changes apply instantly for everyone.
        </p>
      </div>

      <div className="space-y-2">
        <ToggleRow
          icon={<Video size={16} />}
          label="Students can share video"
          description="Allow students to turn on their camera."
          enabled={shown.canPublishVideo}
          disabled={isPending}
          onToggle={() => apply({ canPublishAudio: shown.canPublishAudio, canPublishVideo: !shown.canPublishVideo })}
        />
        <ToggleRow
          icon={<Mic size={16} />}
          label="Students can share audio"
          description="Allow students to unmute their microphone."
          enabled={shown.canPublishAudio}
          disabled={isPending}
          onToggle={() => apply({ canPublishAudio: !shown.canPublishAudio, canPublishVideo: shown.canPublishVideo })}
        />
      </div>
    </div>
  );
};

/**
 * Start/stop recording, live.
 *
 * Stopping is FINAL for the session — that is what keeps the archive one continuous video rather
 * than fragments — so it asks for confirmation and the control then locks itself out. The server
 * enforces the same rule (409 on a restart); this is the humane half of it.
 */
const RecordingControl = ({
  sessionId,
  liveState,
}: {
  sessionId: string;
  liveState: RecordingState | null;
}) => {
  const { data } = useStreamDetails(sessionId);
  const { showToast } = useToast();
  const { mutate, isPending } = useUpdateRecording(sessionId);
  const [confirmingStop, setConfirmingStop] = useState(false);

  // A live SignalR update wins; otherwise the state carried in the initial stream details, which
  // is what makes this correct for a teacher who reloads mid-session.
  const state: RecordingState = liveState ?? data?.recordingState ?? 'Off';

  const apply = (enabled: boolean) => {
    setConfirmingStop(false);
    mutate(enabled, {
      onError: () =>
        showToast({
          type: 'error',
          title: enabled ? 'Could not start recording' : 'Could not stop recording',
          message: 'The change did not go through. Please try again.',
        }),
    });
  };

  if (state === 'Ended') {
    return (
      <div className="rounded-xl border border-white/5 bg-white/5 p-3">
        <p className="text-sm font-medium text-slate-300">Recording finished</p>
        <p className="text-[11px] text-slate-500 leading-tight mt-1">
          This session was recorded and the recording has been stopped. It cannot be restarted —
          the recording will appear in the classroom archive shortly.
        </p>
      </div>
    );
  }

  const isRecording = state === 'Recording';

  return (
    <div
      className={`rounded-xl border p-3 ${
        isRecording ? 'border-red-500/30 bg-red-500/10' : 'border-white/5 bg-white/5'
      }`}
    >
      <div className="flex items-start gap-3">
        <Circle
          size={16}
          className={`mt-0.5 shrink-0 ${isRecording ? 'fill-red-500 text-red-500' : 'text-slate-500'}`}
        />
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium text-slate-200 leading-tight">
            {isRecording ? 'Recording this session' : 'Not recording'}
          </p>
          <p className="text-[11px] text-slate-500 leading-tight mt-0.5">
            {isRecording
              ? 'Everyone in the session can see that it is being recorded.'
              : 'Record this session so students can watch it later.'}
          </p>
        </div>
      </div>

      {confirmingStop ? (
        <div className="mt-3 space-y-2">
          <p className="text-[11px] text-slate-400 leading-tight">
            Stop recording? Recording cannot be resumed for this session.
          </p>
          <div className="flex gap-2">
            <button
              type="button"
              disabled={isPending}
              onClick={() => apply(false)}
              className="flex-1 rounded-lg bg-red-600 px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
            >
              Stop recording
            </button>
            <button
              type="button"
              disabled={isPending}
              onClick={() => setConfirmingStop(false)}
              className="flex-1 rounded-lg bg-slate-700 px-3 py-1.5 text-xs font-medium text-slate-200 disabled:opacity-50"
            >
              Keep recording
            </button>
          </div>
        </div>
      ) : (
        <button
          type="button"
          disabled={isPending}
          onClick={() => (isRecording ? setConfirmingStop(true) : apply(true))}
          className={`mt-3 w-full rounded-lg px-3 py-1.5 text-xs font-medium disabled:opacity-50 ${
            isRecording
              ? 'bg-slate-700 text-slate-200'
              : 'bg-red-600 text-white'
          }`}
        >
          {isPending
            ? 'Working…'
            : isRecording
              ? 'Stop recording'
              : 'Start recording'}
        </button>
      )}
    </div>
  );
};

interface ToggleRowProps {
  icon: React.ReactNode;
  label: string;
  description: string;
  enabled: boolean;
  disabled: boolean;
  onToggle: () => void;
}

const ToggleRow = ({ icon, label, description, enabled, disabled, onToggle }: ToggleRowProps) => (
  <div className="flex items-center justify-between gap-3 rounded-xl border border-white/5 bg-white/5 p-3">
    <div className="flex items-start gap-3 min-w-0">
      <span className={`mt-0.5 ${enabled ? 'text-violet-400' : 'text-slate-500'}`}>{icon}</span>
      <div className="min-w-0">
        <p className="text-sm font-medium text-slate-200 leading-tight">{label}</p>
        <p className="text-[11px] text-slate-500 leading-tight mt-0.5">{description}</p>
      </div>
    </div>
    <button
      type="button"
      role="switch"
      aria-checked={enabled}
      aria-label={label}
      disabled={disabled}
      onClick={onToggle}
      className={`relative h-6 w-11 shrink-0 rounded-full transition-colors outline-none disabled:opacity-50 ${
        enabled ? 'bg-violet-600' : 'bg-slate-700'
      }`}
    >
      <span
        className={`absolute top-0.5 h-5 w-5 rounded-full bg-white shadow transition-transform ${
          enabled ? 'translate-x-[22px]' : 'translate-x-0.5'
        }`}
      />
    </button>
  </div>
);
