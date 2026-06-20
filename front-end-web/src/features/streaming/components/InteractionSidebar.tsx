import { useState, useRef, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router-dom';
import { MessageSquare, Send, HelpCircle } from 'lucide-react';
import { useStreamHub } from '../hooks/useStreamHub';
import { Button } from '../../../components/ui/Button';
import { Tabs } from '../../../components/ui/Tabs';
import { useAuthStore } from '../../../store/useAuthStore';

export const InteractionSidebar = () => {
  const { t } = useTranslation('streaming');
  const { sessionId } = useParams<{ sessionId: string }>();
  const { user } = useAuthStore();
  const { messages, sendMessage, isConnected } = useStreamHub(sessionId);
  
  const [activeTab, setActiveTab] = useState('chat');
  const [inputText, setInputText] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  // Smooth scroll to bottom on new message
  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTo({
        top: scrollRef.current.scrollHeight,
        behavior: 'smooth'
      });
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
  ];

  return (
    <aside className="flex h-full w-80 flex-col border-l border-white/10 bg-slate-900 shadow-2xl z-20">
      {/* Tab Selection */}
      <div className="p-2 border-b border-white/10 bg-slate-950/50">
        <Tabs tabs={tabs} activeTab={activeTab} onChange={setActiveTab} />
      </div>

      {/* Scrollable Content Area */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto min-h-0 bg-slate-900/30">
        {activeTab === 'chat' ? (
          <div className="p-4 space-y-4">
            {messages.length === 0 ? (
              <div className="flex flex-col items-center justify-center h-64 text-slate-600">
                <MessageSquare size={40} className="opacity-10 mb-2" />
                <p className="text-xs font-medium">Classroom is quiet... say hi!</p>
              </div>
            ) : (
              messages.map((m, index) => {
                const isMe = m.userId === user?.id;
                return (
                  <div key={index} className={`flex flex-col ${isMe ? 'items-end' : 'items-start'}`}>
                    <span className="text-[10px] font-bold text-slate-500 uppercase px-1 mb-1 tracking-wider">
                      {isMe ? 'You' : m.userName}
                    </span>
                    <div className={`max-w-[90%] rounded-2xl p-3 shadow-md ${
                      isMe 
                        ? 'bg-violet-600 text-white rounded-tr-none' 
                        : 'bg-slate-800 text-slate-200 rounded-tl-none border border-white/5'
                    }`}>
                      <p className="text-sm break-words leading-relaxed">{m.message}</p>
                    </div>
                  </div>
                );
              })
            )}
          </div>
        ) : (
          <div className="p-8 text-center flex flex-col items-center justify-center h-full">
            <HelpCircle className="text-slate-700 mb-4" size={48} />
            <h3 className="text-sm font-bold text-white mb-1">Questions & Answers</h3>
            <p className="text-xs text-slate-500 mb-6">Ask a question for the teacher to see.</p>
            <Button variant="outline" className="w-full border-white/10 text-white hover:bg-white/5">
                Ask a Question
            </Button>
          </div>
        )}
      </div>

      {/* Fixed Chat Input Area */}
      {activeTab === 'chat' && (
        <div className="p-4 border-t border-white/10 bg-slate-950">
          <form onSubmit={handleSend} className="flex gap-2">
            <input
              value={inputText}
              onChange={(e) => setInputText(e.target.value)}
              disabled={!isConnected}
              placeholder={isConnected ? "Type message..." : "Connecting..."}
              className="flex-1 rounded-xl border border-white/10 bg-white/5 px-4 py-2 text-sm text-white outline-none focus:border-violet-500 focus:ring-1 focus:ring-violet-500 transition-all"
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