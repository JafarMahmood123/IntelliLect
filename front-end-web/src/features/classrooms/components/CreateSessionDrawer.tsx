import { useState, type SetStateAction } from "react";
import { useForm } from "react-hook-form";
import { Calendar as CalendarIcon } from "lucide-react";
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

export const CreateSessionDrawer = ({
  isOpen,
  onClose,
  classroomId,
}: CreateSessionDrawerProps) => {
  const { showToast } = useToast();
  const createSessionMutation = useCreateSession(classroomId);

  // Single state for both Date and Time
  const [startDate, setStartDate] = useState<Date | null>(new Date());

  const {
    register,
    handleSubmit,
    reset,
  } = useForm<{ title: string; description: string }>();

  const onSubmit = async (data: { title: string; description: string }) => {
    if (!startDate) return;

    try {
      await createSessionMutation.mutateAsync({
        title: data.title,
        description: data.description,
        scheduledAtUtc: startDate.toISOString(),
      });

      showToast({
        type: "success",
        title: "Success",
        message: "Session scheduled!",
      });
      reset();
      onClose();
    } catch (error) {
      showToast({
        type: "error",
        title: "Error",
        message: "Failed to schedule.",
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
                className="w-full rounded-xl border border-slate-800 bg-slate-900 py-2.5 pl-11 pr-4 text-slate-100 outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
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
