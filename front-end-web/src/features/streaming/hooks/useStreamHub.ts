import { useEffect, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';
import { useAuthStore } from '../../../store/useAuthStore';

export const useStreamHub = (sessionId: string | undefined) => {
  const [isConnected, setIsConnected] = useState(false);
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const accessToken = localStorage.getItem('accessToken');

  useEffect(() => {
    if (!sessionId || !accessToken) return;

    // 1. Initialize Connection
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`/hubs/stream?access_token=${accessToken}`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

    // 2. Start Connection
    const startConnection = async () => {
      try {
        await connection.start();
        console.log('Connected to StreamHub');
        
        // Join the specific session group
        await connection.invoke('JoinStreamRoom', sessionId);
        setIsConnected(true);
      } catch (err) {
        console.error('SignalR Connection Error: ', err);
      }
    };

    startConnection();

    // 3. Cleanup on unmount
    return () => {
      if (connectionRef.current) {
        connectionRef.current.stop();
      }
    };
  }, [sessionId, accessToken]);

  return { isConnected, hub: connectionRef.current };
};