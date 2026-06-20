import { useState, useRef, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router-dom';
import { MessageSquare, Send } from 'lucide-react';
import { useStreamHub } from '../hooks/useStreamHub';
import { Button } from '../../../components/ui/Button';

export const InteractionSidebar = () => {
  const { t } = useTranslation('streaming');
  const { sessionId } = useParams<{ sessionId: string }>();
  const { messages, sendMessage, isConnected } = useStreamHub(sessionId);
  const [inputText, setInputText] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  // Auto-scroll chat to bottom when new messages arrive
  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [messages]);

  const handleSend = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!inputText.trim() || !isConnected) return;

    await sendMessage(inputText);
    setInputText('');
  };

  return (
    <aside
      className="flex h-full flex-col border-l border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900 lg:w-80 lg:shrink-0"
      aria-label={t('sidebar.ariaLabel')}
    >
      {/* Header */}
      <div className="flex items-center gap-2 border-b border-slate-200 p-4 dark:border-slate-800">
        <MessageSquare size={18} className="text-violet-600 dark:text-violet-400" />
        <h2 className="text-sm font-bold text-slate-900 dark:text-white">Classroom Chat</h2>
      </div>

      {/* Messages Area */}
      <div 
        ref={scrollRef}
        className="flex-1 overflow-y-auto p-4 space-y-4 min-h-0"
      >
        {messages.length === 0 ? (
          <p className="text-center text-xs text-slate-500 mt-10">No messages yet. Start the conversation!</p>
        ) : (
          messages.map((m, index) => (
            <div key={index} className="flex flex-col">
              <span className="text-[10px] font-bold text-violet-600 dark:text-violet-400 uppercase tracking-tight">
                {m.userName}
              </span>
              <div className="mt-1 rounded-2xl rounded-tl-none bg-slate-100 p-3 dark:bg-slate-800">
                <p className="text-sm text-slate-800 dark:text-slate-200 break-words leading-relaxed">
                  {m.message}
                </p>
              </div>
            </div>
          ))
        )}
      </div>

      {/* Input Area */}
      <div className="border-t border-slate-200 p-4 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-950/50">
        <form onSubmit={handleSend} className="flex gap-2">
          <input
            type="text"
            value={inputText}
            onChange={(e) => setInputText(e.target.value)}
            disabled={!isConnected}
            placeholder={isConnected ? "Type a message..." : "Connecting..."}
            className="flex-1 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-violet-500 focus:ring-1 focus:ring-violet-500 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
          />
          <Button 
            type="submit" 
            disabled={!isConnected || !inputText.trim()}
            className="!p-2 h-9 w-9 shrink-0 rounded-xl"
          >
            <Send size={16} />
          </Button>
        </form>
        {!isConnected && (
            <p className="mt-2 text-[10px] text-amber-600 text-center animate-pulse">
                Connection to chat lost. Reconnecting...
            </p>
        )}
      </div>
    </aside>
  );
};