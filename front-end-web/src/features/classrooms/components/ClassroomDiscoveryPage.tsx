import { Compass } from "lucide-react";
import { PageHeader } from "../../../components/ui/PageHeader";
import { ClassroomCard } from "./ClassroomCard";
import {
  useDiscoveryClassrooms,
  useEnrollClassroom,
  useEnrolledClassrooms,
} from "../hooks/useClassroomQueries";
import { Button } from "../../../components/ui/Button";
import { useToast } from "../../../components/ui/ToastProvider";
import { useState } from "react";

export const ClassroomDiscoveryPage = () => {
  const { showToast } = useToast();
  const [currentPage, setCurrentPage] = useState(1);
  
  // Pass the page to the updated hook
  const { data: pagedData, isLoading } = useDiscoveryClassrooms(currentPage);
  const { data: enrolled } = useEnrolledClassrooms();
  const enrollMutation = useEnrollClassroom();

  const enrolledIds = new Set(enrolled?.map((c) => c.id));
  
  // Correctly access .items and provide fallback array
  const availableClassrooms =
    pagedData?.items.filter((c) => !enrolledIds.has(c.id)) ?? [];

  const handleEnroll = async (classroomId: string, name: string) => {
    try {
      await enrollMutation.mutateAsync(classroomId);
      showToast({
        type: "success",
        title: "Enrolled!",
        message: `You have successfully joined ${name}.`,
      });
    } catch (error) {
      showToast({
        type: "error",
        title: "Enrollment Failed",
        message: "Could not join the classroom at this time.",
      });
    }
  };

  return (
    <div className="mx-auto w-full max-w-6xl p-6">
      <PageHeader
        title="Discover Classrooms"
        description="Browse available subjects and join new learning communities."
      />

      {isLoading ? (
        <div className="grid gap-6 md:grid-cols-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-48 animate-pulse rounded-2xl bg-slate-100 dark:bg-slate-800" />
          ))}
        </div>
      ) : availableClassrooms.length === 0 ? (
        <div className="py-20 text-center">
          <Compass className="mx-auto mb-4 text-slate-300" size={48} />
          <p className="text-slate-500">No new classrooms available to join right now.</p>
        </div>
      ) : (
        <>
          <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
            {availableClassrooms.map((classroom) => (
              <div key={classroom.id} className="relative group">
                <ClassroomCard classroom={classroom} onClick={() => {}} />
                <div className="absolute top-4 right-4 opacity-0 group-hover:opacity-100 transition-opacity">
                  <Button
                    isLoading={enrollMutation.isPending && enrollMutation.variables === classroom.id}
                    onClick={() => handleEnroll(classroom.id, classroom.name)}
                  >
                    Join Class
                  </Button>
                </div>
              </div>
            ))}
          </div>

          {/* Pagination Controls */}
          {pagedData && pagedData.totalPages > 1 && (
            <div className="mt-12 flex items-center justify-center gap-4">
              <Button
                variant="secondary"
                disabled={!pagedData.hasPreviousPage}
                onClick={() => setCurrentPage(p => p - 1)}
              >
                Previous
              </Button>
              <span className="text-sm font-medium text-slate-500">
                Page {pagedData.pageNumber} of {pagedData.totalPages}
              </span>
              <Button
                variant="secondary"
                disabled={!pagedData.hasNextPage}
                onClick={() => setCurrentPage(p => p + 1)}
              >
                Next
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
};