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
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const accessToken = localStorage.getItem('accessToken');

  useEffect(() => {
    if (!sessionId || !accessToken) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`/hubs/stream?access_token=${accessToken}`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .build();

    // Listener for real-time chat messages
    connection.on('ReceiveChatMessage', (userId: string, userName: string, message: string) => {
      setMessages((prev) => [...prev, { userId, userName, message, timestamp: new Date() }]);
    });

    const startConnection = async () => {
      try {
        await connection.start();
        console.log('Connected to StreamHub');
        
        await connection.invoke('JoinStreamRoom', sessionId);
        setIsConnected(true);
      } catch (err) {
        console.error('SignalR Connection Error: ', err);
      }
    };

    connectionRef.current = connection;
    startConnection();

    return () => {
      if (connectionRef.current) {
        connectionRef.current.stop();
      }
    };
  }, [sessionId, accessToken]);

  const sendMessage = useCallback(async (msg: string) => {
    if (connectionRef.current && isConnected && sessionId) {
      try {
        await connectionRef.current.invoke('SendChatMessage', sessionId, msg);
      } catch (err) {
        console.error('Failed to send message:', err);
      }
    }
  }, [sessionId, isConnected]);

  return { isConnected, messages, sendMessage, hub: connectionRef.current };
};