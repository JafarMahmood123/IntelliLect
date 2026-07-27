import { Mic, Video } from 'lucide-react';
import { useStreamDetails, useUpdatePublishPolicy } from '../hooks/useStreamingQueries';
import { useToast } from '../../../components/ui/ToastProvider';
import type { PublishPolicy } from '../types';

interface SessionSettingsPanelProps {
  sessionId: string;
  /** Latest policy pushed over SignalR (null until the first change this session). */
  livePolicy: PublishPolicy | null;
}

/**
 * Teacher-only in-session controls. Lets the teacher toggle, in real time, whether students may
 * share their camera and microphone. Each toggle calls the backend, which enforces the change on
 * already-connected students (force-stopping a now-forbidden track) and broadcasts it so every
 * client updates immediately.
 */
export const SessionSettingsPanel = ({ sessionId, livePolicy }: SessionSettingsPanelProps) => {
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
