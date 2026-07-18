import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, File as FileIcon, Loader2, Trash2, UploadCloud } from 'lucide-react';
import { Table, type TableColumn } from '../../../components/ui/Table';
import { Button } from '../../../components/ui/Button';
import { ConfirmationModal } from '../../../components/ui/ConfirmationModal';
import { useToast } from '../../../components/ui/ToastProvider';
import { useClassroomFiles, useDeleteClassroomFile, useUploadClassroomFile } from '../hooks/useClassroomQueries';
import { downloadClassroomFile } from '../api/classrooms';
import { FileIndexingBadge } from './FileIndexingBadge';
import type { ClassroomFile } from '../types';

/**
 * Downloads a file by streaming it through the API (auth header + blob), showing a spinner while
 * in flight and a toast on failure. Kept self-contained so each row tracks its own state.
 */
const FileDownloadButton = ({ classroomId, file }: { classroomId: string; file: ClassroomFile }) => {
  const { showToast } = useToast();
  const [isDownloading, setIsDownloading] = useState(false);

  const handleClick = async () => {
    if (isDownloading) return;
    setIsDownloading(true);
    try {
      await downloadClassroomFile(classroomId, file.id, file.fileName);
    } catch {
      showToast({ type: 'error', title: 'Download failed', message: 'Could not download this file.' });
    } finally {
      setIsDownloading(false);
    }
  };

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={isDownloading}
      aria-label={`Download ${file.fileName}`}
      aria-busy={isDownloading}
      title="Download File"
      className="inline-flex h-9 items-center justify-center rounded-lg border border-slate-200 bg-white px-3 text-slate-600 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900 active:scale-[0.98]"
    >
      {isDownloading ? <Loader2 size={16} className="animate-spin" /> : <Download size={16} />}
    </button>
  );
};

interface ClassroomFileListProps {
  classroomId: string;
  isTeacher: boolean;
}

const formatBytes = (bytes: number) => {
  if (bytes === 0) return '0 Bytes';
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
};

export const ClassroomFileList = ({ classroomId, isTeacher }: ClassroomFileListProps) => {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const { showToast } = useToast();
  const { t } = useTranslation('common');
  
  const [fileToDelete, setFileToDelete] = useState<ClassroomFile | null>(null);

  const { data: files = [], isLoading, isError } = useClassroomFiles(classroomId);
  const uploadMutation = useUploadClassroomFile(classroomId);
  const deleteMutation = useDeleteClassroomFile(classroomId);

  const handleFileChange = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;

    try {
      await uploadMutation.mutateAsync(file);
      showToast({ type: 'success', title: 'File Uploaded', message: `${file.name} was successfully uploaded.` });
    } catch (error) {
      showToast({ type: 'error', title: 'Upload Failed', message: 'Something went wrong while uploading the file.' });
    } finally {
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  };

  const handleConfirmDelete = async () => {
    if (!fileToDelete) return;
    try {
      await deleteMutation.mutateAsync(fileToDelete.id);
      showToast({ type: 'success', title: 'File Deleted', message: 'The file has been removed.' });
      setFileToDelete(null);
    } catch (error) {
      showToast({ type: 'error', title: 'Delete Failed', message: 'Could not delete the file.' });
    }
  };

  const columns: TableColumn<ClassroomFile>[] = [
    {
      key: 'name',
      header: 'File Name',
      render: (file) => (
        <div className="flex items-center gap-3">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-violet-50 text-violet-600 dark:bg-violet-900/30 dark:text-violet-400">
            <FileIcon size={18} />
          </div>
          <div>
            <p className="font-medium text-slate-900 dark:text-slate-100">{file.fileName}</p>
            <p className="text-xs text-slate-500">{file.contentType}</p>
          </div>
        </div>
      ),
    },
    {
      key: 'size',
      header: 'Size',
      render: (file) => <span className="text-slate-600 dark:text-slate-400">{formatBytes(file.sizeBytes)}</span>,
    },
    {
      key: 'indexing',
      header: t('indexing.columnHeader'),
      render: (file) => <FileIndexingBadge classroomId={classroomId} fileId={file.id} />,
    },
    {
      key: 'actions',
      header: 'Actions',
      headerClassName: 'text-right',
      cellClassName: 'text-right',
      render: (file) => (
        <div className="flex justify-end gap-2">
          {/* Streams through the API/gateway (auth + blob) — the browser never touches the raw
              MinIO port, so no scheme/HTTPS-upgrade issues. */}
          <FileDownloadButton classroomId={classroomId} file={file} />

          {isTeacher && (
            <button
              onClick={() => setFileToDelete(file)}
              disabled={deleteMutation.isPending}
              className={`inline-flex h-9 items-center justify-center rounded-lg border border-red-200 bg-red-50 px-3 text-red-600 transition-all hover:bg-red-100 dark:border-red-900/50 dark:bg-red-950/30 dark:text-red-400 dark:hover:bg-red-950/50 ${
                deleteMutation.isPending ? 'cursor-not-allowed opacity-60' : 'cursor-pointer active:scale-[0.98]'
              }`}
              title="Delete File"
            >
              <Trash2 size={16} />
            </button>
          )}
        </div>
      ),
    },
  ];

  return (
    <div className="space-y-4">
      {isTeacher && (
        <div className="flex justify-end">
          <input 
            type="file" 
            ref={fileInputRef} 
            onChange={handleFileChange} 
            className="hidden" 
          />
          <Button 
            onClick={() => fileInputRef.current?.click()} 
            isLoading={uploadMutation.isPending}
          >
            <UploadCloud size={18} />
            Upload Material
          </Button>
        </div>
      )}

      <Table
        data={files}
        columns={columns}
        rowKey={(file) => file.id}
        isLoading={isLoading}
        isError={isError}
        emptyText={isTeacher ? "No materials uploaded yet. Click above to upload." : "No materials available for this class yet."}
      />

      <ConfirmationModal
        isOpen={!!fileToDelete}
        onClose={() => setFileToDelete(null)}
        onConfirm={handleConfirmDelete}
        isLoading={deleteMutation.isPending}
        title="Delete Material"
        description={`Are you sure you want to delete "${fileToDelete?.fileName}"? This action cannot be undone.`}
        confirmText="Yes, Delete"
        variant="danger"
      />
    </div>
  );
};