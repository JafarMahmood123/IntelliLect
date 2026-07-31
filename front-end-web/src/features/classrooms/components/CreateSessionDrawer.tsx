import { useState, type SetStateAction } from "react";
import { useForm } from "react-hook-form";
import { Calendar as CalendarIcon, Circle, ShieldCheck } from "lucide-react";
import DatePicker from "react-datepicker";
import "react-datepicker/dist/react-datepicker.css";
import { Drawer } from "../../../components/ui/Drawer";
import { Button } from "../../../components/ui/Button";
import { useToast } from "../../../components/ui/ToastProvider";
import { useCreateSession } from "../hooks/useClassroomQueries";

interface CreateSessionDrawerProps {
  isOpen: boolean;
  onClose: () => void;
  classroomId: string;
}

interface CreateSessionForm {
  title: string;
  description: string;
  participationMode: number;
  recordingEnabled: boolean;
}

export const CreateSessionDrawer = ({
  isOpen,
  onClose,
  classroomId,
}: CreateSessionDrawerProps) => {
  const { showToast } = useToast();
  const createSessionMutation = useCreateSession(classroomId);

  const [startDate, setStartDate] = useState<Date | null>(new Date());

  const {
    register,
    handleSubmit,
    reset,
  } = useForm<CreateSessionForm>({
    defaultValues: {
      participationMode: 0,
      // Recording is opt-in: nothing is captured unless the teacher asks for it.
      recordingEnabled: false,
    }
  });

  const onSubmit = async (data: CreateSessionForm) => {
    if (!startDate) return;

    try {
      await createSessionMutation.mutateAsync({
        title: data.title,
        description: data.description,
        scheduledAtUtc: startDate.toISOString(),
        participationMode: Number(data.participationMode), // Ensure it's a number
        recordingEnabled: data.recordingEnabled,
      });

      showToast({
        type: "success",
        title: "Success",
        message: "Session scheduled successfully!",
      });
      reset();
      onClose();
    } catch (error) {
      showToast({
        type: "error",
        title: "Error",
        message: "Failed to schedule the session.",
      });
    }
  };

  return (
    <Drawer isOpen={isOpen} onClose={onClose} title="Schedule New Session">
      <form
        onSubmit={handleSubmit(onSubmit)}
        className="flex flex-col gap-6 p-6"
      >
        <div className="space-y-5">
          {/* Title */}
          <div className="space-y-1.5">
            <label className="text-sm font-semibold text-slate-700 dark:text-slate-300">
              Session Title
            </label>
            <input
              {...register("title", { required: "Title is required" })}
              className="w-full rounded-xl border border-slate-200 bg-white px-4 py-2.5 outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
              placeholder="e.g. Midterm Review"
            />
          </div>

          {/* Description */}
          <div className="space-y-1.5">
            <label className="text-sm font-semibold text-slate-700 dark:text-slate-300">
              Description
            </label>
            <textarea
              {...register("description")}
              rows={2}
              className="w-full rounded-xl border border-slate-200 bg-white px-4 py-2.5 outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
              placeholder="What is this session about?"
            />
          </div>

          {/* Participation Mode */}
          <div className="space-y-1.5">
            <label className="text-sm font-semibold text-slate-700 dark:text-slate-300 flex items-center gap-2">
              <ShieldCheck size={16} className="text-violet-500" />
              Student Participation
            </label>
            <select
              {...register("participationMode", { valueAsNumber: true })}
              className="w-full rounded-xl border border-slate-200 bg-white px-4 py-2.5 outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500 dark:border-slate-800 dark:bg-slate-900 dark:text-white appearance-none"
            >
              <option value={0}>View Only (Students cannot share)</option>
              <option value={1}>Voice Only (Students can speak)</option>
              <option value={2}>Voice & Video (Full participation)</option>
            </select>
            <p className="text-[11px] text-slate-500 px-1">
              This controls which devices students can activate during the live stream.
            </p>
          </div>

          {/* Recording. Opt-in, and only the starting point — the teacher can start recording
              later from inside the session. Stopping it there is final. */}
          <div className="space-y-1.5">
            <label className="flex items-start gap-3 rounded-xl border border-slate-200 bg-white px-4 py-3 cursor-pointer dark:border-slate-800 dark:bg-slate-900">
              <input
                type="checkbox"
                {...register("recordingEnabled")}
                className="mt-0.5 h-4 w-4 rounded border-slate-300 text-violet-600 focus:ring-violet-500/20 dark:border-slate-700"
              />
              <span className="min-w-0">
                <span className="flex items-center gap-2 text-sm font-semibold text-slate-700 dark:text-slate-300">
                  <Circle size={14} className="text-red-500 fill-red-500" />
                  Record this session
                </span>
                <span className="mt-0.5 block text-[11px] text-slate-500">
                  Saves the session to the classroom archive so students can watch it later. You can
                  also start recording during the session; once stopped, it cannot be resumed.
                </span>
              </span>
            </label>
          </div>

          {/* Integrated Date & Time Picker */}
          <div className="space-y-1.5">
            <label className="text-sm font-semibold text-slate-700 dark:text-slate-300">
              Scheduled For
            </label>
            <div className="relative">
              <CalendarIcon
                className="absolute left-4 top-3 z-10 text-slate-400"
                size={18}
              />
              <DatePicker
                selected={startDate}
                onChange={(date: SetStateAction<Date | null>) =>
                  setStartDate(date)
                }
                showTimeSelect
                timeIntervals={30}
                timeCaption="Time"
                dateFormat="MMMM d, yyyy h:mm aa"
                minDate={new Date()}
                calendarClassName="dark-datepicker"
                className="w-full rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900 py-2.5 pl-11 pr-4 text-slate-900 dark:text-slate-100 outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
                placeholderText="Select date and time"
              />
            </div>
          </div>
        </div>

        <div className="mt-6 flex gap-3">
          <Button variant="outline" fullWidth onClick={onClose}>
            Cancel
          </Button>
          <Button
            type="submit"
            fullWidth
            isLoading={createSessionMutation.isPending}
          >
            Schedule Session
          </Button>
        </div>
      </form>
    </Drawer>
  );
};