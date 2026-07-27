import { useState, useRef, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { MessageSquare, Send, HelpCircle, SlidersHorizontal } from 'lucide-react';
import { useStreamHub } from '../hooks/useStreamHub';
import { Button } from '../../../components/ui/Button';
import { Tabs } from '../../../components/ui/Tabs';
import { useAuthStore } from '../../../store/useAuthStore';
import { SessionSettingsPanel } from './SessionSettingsPanel';

export const InteractionSidebar = () => {
  const { sessionId } = useParams<{ sessionId: string }>();
  const { user } = useAuthStore();
  const { messages, sendMessage, isConnected, publishPolicy } = useStreamHub(sessionId);
  const isTeacher = user?.roleName === 'Teacher';

  const [activeTab, setActiveTab] = useState('chat');
  const [inputText, setInputText] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [messages, activeTab]);

  const handleSend = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!inputText.trim() || !isConnected) return;
    await sendMessage(inputText);
    setInputText('');
  };

  const tabs = [
    { id: 'chat', label: 'Chat', icon: <MessageSquare size={16} /> },
    { id: 'qa', label: 'Q&A', icon: <HelpCircle size={16} /> },
    // The teacher's live student-permission controls; hidden from students.
    ...(isTeacher
      ? [{ id: 'settings', label: 'Session Settings', icon: <SlidersHorizontal size={16} /> }]
      : []),
  ];

  return (
    <aside className="flex h-full w-80 flex-col border-l border-white/10 bg-slate-900 shadow-2xl z-20 overflow-hidden">
      {/* Fixed Tab Area */}
      <div className="p-2 border-b border-white/5 bg-slate-950/50 flex-shrink-0">
        <Tabs tabs={tabs} activeTab={activeTab} onChange={setActiveTab} />
      </div>

      {/* Scrollable Message List */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto min-h-0 bg-slate-900/20 custom-scrollbar">
        {activeTab === 'settings' && isTeacher && sessionId ? (
          <SessionSettingsPanel sessionId={sessionId} livePolicy={publishPolicy} />
        ) : activeTab === 'chat' ? (
          <div className="p-4 space-y-5">
            {messages.length === 0 ? (
              <div className="flex flex-col items-center justify-center h-40 text-slate-700">
                <MessageSquare size={32} className="opacity-20 mb-2" />
                <p className="text-[10px] uppercase font-bold tracking-tighter">No messages yet</p>
              </div>
            ) : (
              messages.map((m, index) => {
                const isMe = m.userId === user?.id;
                return (
                  <div key={index} className={`flex flex-col ${isMe ? 'items-end' : 'items-start'}`}>
                    <span className="text-[9px] font-bold text-slate-500 uppercase px-1 mb-1">
                      {isMe ? 'You' : m.userName}
                    </span>
                    <div className={`max-w-[92%] rounded-2xl p-3 shadow-sm ${
                      isMe 
                        ? 'bg-violet-600 text-white rounded-tr-none' 
                        : 'bg-slate-800 text-slate-200 rounded-tl-none border border-white/5'
                    }`}>
                      <p className="text-sm break-words leading-tight">{m.message}</p>
                    </div>
                  </div>
                );
              })
            )}
          </div>
        ) : (
          <div className="p-8 text-center flex flex-col items-center justify-center h-full">
            <HelpCircle className="text-slate-800 mb-4" size={48} />
            <h3 className="text-sm font-bold text-slate-200 mb-1">Q&A</h3>
            <p className="text-[11px] text-slate-500 mb-6">This feature is coming soon.</p>
          </div>
        )}
      </div>

      {/* Fixed Bottom Input */}
      {activeTab === 'chat' && (
        <div className="p-4 border-t border-white/5 bg-slate-950 flex-shrink-0">
          <form onSubmit={handleSend} className="flex gap-2">
            <input
              value={inputText}
              onChange={(e) => setInputText(e.target.value)}
              disabled={!isConnected}
              placeholder={isConnected ? "Say something..." : "Reconnecting..."}
              className="flex-1 rounded-xl border border-white/10 bg-white/5 px-4 py-2 text-sm text-white placeholder:text-slate-600 outline-none focus:border-violet-500 transition-all"
            />
            <Button 
                type="submit" 
                disabled={!isConnected || !inputText.trim()} 
                className="!p-0 h-10 w-10 shrink-0 rounded-xl flex items-center justify-center"
            >
              <Send size={18} />
            </Button>
          </form>
        </div>
      )}
    </aside>
  );
};