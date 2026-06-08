import { useNavigate } from "react-router-dom";
import { BookOpen, Plus } from "lucide-react";
import { PageHeader } from "../../../components/ui/PageHeader";
import { Button } from "../../../components/ui/Button";
import { useEnrolledClassrooms } from "../hooks/useClassroomQueries";
import { ClassroomCard } from "./ClassroomCard";

export const StudentClassroomDashboard = () => {
  const navigate = useNavigate();
  const { data: classrooms, isLoading } = useEnrolledClassrooms();

  if (isLoading)
    return <div className="p-10 text-center">Loading your classes...</div>;

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader
        title="My Learning"
        description="Access your enrolled classrooms and upcoming sessions."
        action={
          <Button
            variant="secondary"
            onClick={() => navigate("/classrooms/discover")}
          >
            <Plus size={18} />
            Find More Classes
          </Button>
        }
      />

      {!classrooms || classrooms.length === 0 ? (
        <div className="mt-12 flex flex-col items-center justify-center rounded-2xl border border-dashed border-slate-300 py-20 dark:border-slate-800">
          <BookOpen className="mb-4 text-violet-500 opacity-20" size={64} />
          <h3 className="text-xl font-bold">
            You aren't enrolled in any classes yet
          </h3>
          <p className="mt-2 text-slate-500">
            Discover new subjects and start your learning journey.
          </p>
          <Button
            className="mt-6"
            onClick={() => navigate("/classrooms/discover")}
          >
            Browse Classrooms
          </Button>
        </div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
          {classrooms.map((c) => (
            <ClassroomCard
              key={c.id}
              classroom={c}
              onClick={(id) => navigate(`/classrooms/${id}`)}
            />
          ))}
        </div>
      )}
    </div>
  );
};
