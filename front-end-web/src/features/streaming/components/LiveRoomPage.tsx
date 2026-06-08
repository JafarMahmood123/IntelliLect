import { LiveKitRoom, VideoConference } from "@livekit/components-react";
import React, { useCallback, useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate, useParams } from "react-router-dom";
import { PageHeader } from "../../../components/ui/PageHeader";
import { Button } from "../../../components/ui/Button";
import { StatusBadge } from "../../../components/ui/StatusBadge";
import {
  useJoinStream,
  useLeaveStream,
  useStreamDetails,
} from "../hooks/useStreamingQueries";
import { InteractionSidebar } from "./InteractionSidebar";

const toLiveKitServerUrl = (host: string): string => {
  const trimmed = host.trim();
  if (trimmed.startsWith("ws://") || trimmed.startsWith("wss://")) {
    return trimmed;
  }

  try {
    const withProtocol = /^https?:\/\//i.test(trimmed)
      ? trimmed
      : `https://${trimmed}`;
    const url = new URL(withProtocol);
    const protocol = url.protocol === "https:" ? "wss:" : "ws:";
    const path = url.pathname === "/" ? "" : url.pathname;
    return `${protocol}//${url.host}${path}${url.search}`;
  } catch {
    return trimmed;
  }
};

export const LiveRoomPage = () => {
  const { t } = useTranslation("streaming");
  const { classroomId, sessionId = "" } = useParams<{
    classroomId: string;
    sessionId: string;
  }>();
  const navigate = useNavigate();

  const { data, isPending, isError, error, refetch } =
    useStreamDetails(sessionId);
  const { mutateAsync: joinStreamAsync } = useJoinStream();
  const { mutateAsync: leaveStreamAsync } = useLeaveStream();

  const hasJoinedRef = useRef(false);

  const serverUrl = useMemo(
    () => (data?.liveKitHost ? toLiveKitServerUrl(data.liveKitHost) : ""),
    [data?.liveKitHost],
  );

  const token = data?.joinToken ?? "";

  const handleBack = useCallback(() => {
    if (classroomId) {
      navigate(`/classrooms/${classroomId}`);
      return;
    }
    navigate(-1);
  }, [classroomId, navigate]);

  useEffect(() => {
    if (!sessionId || !data?.joinToken) {
      return;
    }

    if (!hasJoinedRef.current) {
      hasJoinedRef.current = true;
      joinStreamAsync(sessionId).catch(() => {
        /* join errors surface */
      });
    }

    return () => {
      leaveStreamAsync(sessionId).catch(() => {
        /* best-effort leave */
      });
    };
  }, [sessionId, data?.joinToken, joinStreamAsync, leaveStreamAsync]);

  const headerAction = (
    <Button type="button" variant="secondary" onClick={handleBack}>
      {t("actions.back")}
    </Button>
  );

  if (!sessionId) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8">
        <PageHeader
          title={t("errors.missingSessionTitle")}
          description={t("errors.missingSessionDescription")}
        />
      </div>
    );
  }

  if (isPending && !data) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8">
        <PageHeader
          title={t("page.title")}
          description={t("page.loadingDescription")}
          action={headerAction}
        />
        <p className="text-sm text-slate-600 dark:text-slate-400">
          {t("states.loading")}
        </p>
      </div>
    );
  }

  if (isError || !data) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8">
        <PageHeader
          title={t("page.title")}
          description={t("errors.loadFailedDescription")}
          action={headerAction}
        />
        <p className="mb-4 text-sm text-red-600 dark:text-red-400" role="alert">
          {error instanceof Error ? error.message : t("errors.generic")}
        </p>
        <Button type="button" onClick={() => void refetch()}>
          {t("actions.retry")}
        </Button>
      </div>
    );
  }

  return (
    <div className="flex min-h-screen flex-col">
      <div className="border-b border-slate-200 bg-white px-4 py-4 dark:border-slate-800 dark:bg-slate-950 lg:px-8">
        <PageHeader
          title={t("page.title")}
          description={t("page.description")}
          action={
            <div className="flex flex-wrap items-center gap-2">
              <StatusBadge status={data.status} />
              {headerAction}
            </div>
          }
        />
      </div>

      <div className="flex flex-1 flex-col overflow-hidden lg:flex-row">
        <main className="flex min-h-0 flex-1 flex-col bg-slate-950 p-2 lg:p-4">
          {token && serverUrl ? (
            <LiveKitRoom
              serverUrl={serverUrl}
              token={token}
              connect={true}
              audio={false}
              video={false}
            >
              <div className="flex h-[min(70vh,720px)] min-h-[240px] w-full flex-1 flex-col lg:h-auto lg:min-h-0">
                <VideoConference />
              </div>
            </LiveKitRoom>
          ) : (
            <div className="flex flex-1 items-center justify-center text-slate-500">
              Initializing Secure Connection...
            </div>
          )}
        </main>
        <InteractionSidebar />
      </div>
    </div>
  );
};
