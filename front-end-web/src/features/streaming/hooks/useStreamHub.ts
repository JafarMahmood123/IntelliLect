import { useEffect, useRef, useState, useCallback } from "react";
import * as signalR from "@microsoft/signalr";

export interface ChatMessage {
  userId: string;
  userName: string;
  message: string;
  timestamp: Date;
}

export const useStreamHub = (sessionId: string | undefined) => {
  const [isConnected, setIsConnected] = useState(false);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [participantCount, setParticipantCount] = useState(0);
  // Set when the server announces the session is over (the teacher ended it, a super admin
  // force-ended it, or the stalled-session sweep closed it). The media room is torn down right
  // after the broadcast, so this is the participants' only chance at a graceful exit.
  const [endedStatus, setEndedStatus] = useState<string | null>(null);

  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const accessToken = localStorage.getItem("accessToken");

  useEffect(() => {
    if (!sessionId || !accessToken) return;

    let isMounted = true;

    // A new session id starts clean — otherwise a previously ended session would immediately
    // eject the user from the next room they open.
    setEndedStatus(null);

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`/hubs/stream?access_token=${accessToken}`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

    connection.on(
      "ReceiveChatMessage",
      (userId: string, userName: string, message: string) => {
        if (isMounted)
          setMessages((prev) => [
            ...prev,
            { userId, userName, message, timestamp: new Date() },
          ]);
      },
    );

    connection.on("UpdateParticipantCount", (count: number) => {
      if (isMounted) setParticipantCount(count);
    });

    connection.on("StreamStatusChanged", (status: string) => {
      if (!isMounted) return;
      console.log(`[SignalR] Stream status changed: ${status}`);
      if (status?.toLowerCase() === "ended") setEndedStatus(status);
    });

    const startConnection = async () => {
      // 1. Guard against multiple starting attempts
      if (connection.state !== signalR.HubConnectionState.Disconnected) return;

      try {
        await connection.start();

        // 2. If we reach here, the promise succeeded, so we ARE connected.
        // We only check isMounted to ensure the user hasn't left the page
        // while we were waiting for the connection.
        if (isMounted) {
          console.log(`[SignalR] Connected: ${sessionId}`);
          await connection.invoke("JoinStreamRoom", sessionId);
          setIsConnected(true);
        }
      } catch (err) {
        if (isMounted) console.error("[SignalR] Connection Error: ", err);
      }
    };

    startConnection();

    return () => {
      isMounted = false;
      const conn = connectionRef.current;
      if (conn) {
        if (conn.state !== signalR.HubConnectionState.Disconnected) {
          console.log(
            `[SignalR] Stopping connection for session: ${sessionId}`,
          );
          conn.stop().catch(() => {});
        }
        connectionRef.current = null;
      }
      setIsConnected(false);
    };
  }, [sessionId, accessToken]);

  const sendMessage = useCallback(
    async (msg: string) => {
      const conn = connectionRef.current;
      if (
        conn &&
        conn.state === signalR.HubConnectionState.Connected &&
        sessionId
      ) {
        try {
          await conn.invoke("SendChatMessage", sessionId, msg);
        } catch (err) {
          console.error("[SignalR] Failed to send message", err);
        }
      }
    },
    [sessionId],
  );

  return {
    isConnected,
    messages,
    participantCount,
    sendMessage,
    hasEnded: endedStatus !== null,
  };
};
