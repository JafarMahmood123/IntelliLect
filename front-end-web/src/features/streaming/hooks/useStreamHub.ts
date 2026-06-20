import { useEffect, useRef, useState, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';

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
  
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const accessToken = localStorage.getItem('accessToken');

  useEffect(() => {
    // If no session or token, we can't connect, but we must stay in the hook
    if (!sessionId || !accessToken) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`/hubs/stream?access_token=${accessToken}`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

    // SignalR Listeners
    connection.on('ReceiveChatMessage', (userId: string, userName: string, message: string) => {
      setMessages((prev) => [...prev, { userId, userName, message, timestamp: new Date() }]);
    });

    connection.on('UpdateParticipantCount', (count: number) => {
      setParticipantCount(count);
    });

    const startConnection = async () => {
      try {
        if (connection.state === signalR.HubConnectionState.Disconnected) {
          await connection.start();
          console.log('SignalR: Connected to StreamHub');
          await connection.invoke('JoinStreamRoom', sessionId);
          setIsConnected(true);
        }
      } catch (err) {
        console.error('SignalR Connection Error: ', err);
      }
    };

    startConnection();

    return () => {
      if (connectionRef.current) {
        connectionRef.current.stop().catch(() => {});
        connectionRef.current = null;
        setIsConnected(false);
      }
    };
  }, [sessionId, accessToken]);

  const sendMessage = useCallback(async (msg: string) => {
    if (connectionRef.current && connectionRef.current.state === signalR.HubConnectionState.Connected && sessionId) {
      try {
        await connectionRef.current.invoke('SendChatMessage', sessionId, msg);
      } catch (err) {
        console.error('SignalR: Failed to send message', err);
      }
    }
  }, [sessionId]);

  return { isConnected, messages, participantCount, sendMessage };
};