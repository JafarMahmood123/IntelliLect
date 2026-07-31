import { useState, useRef, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import {
  ArrowLeft,
  HelpCircle,
  ListChecks,
  MessageSquare,
  Send,
  SlidersHorizontal,
} from 'lucide-react';
import { useStreamHub } from '../hooks/useStreamHub';
import { Button } from '../../../components/ui/Button';
import { useAuthStore } from '../../../store/useAuthStore';
import { SessionSettingsPanel } from './SessionSettingsPanel';
import { SidebarMenu, type SidebarSection } from './SidebarMenu';
import { TeacherQuizPanel } from '../../quizzes/components/TeacherQuizPanel';
import { StudentQuizPanel } from '../../quizzes/components/StudentQuizPanel';

/**
 * The in-session drawer. It owns no session behaviour of its own — it is the container the
 * individual panels live in, and it shows one of two things: the menu of sections, or one open
 * section with a way back to that menu.
 *
 * Which sections exist depends on the role, so a student is never offered a teacher's control by a
 * button that merely opens an empty panel.
 */
export const InteractionSidebar = () => {
  const { classroomId, sessionId } = useParams<{ classroomId: string; sessionId: string }>();
  const { user } = useAuthStore();
  const { messages, sendMessage, isConnected, publishPolicy, recordingState, quizEvent } = useStreamHub(sessionId);
  const isTeacher = user?.roleName === 'Teacher';

  /** `null` is the menu. Opening a section replaces the whole drawer body with it. */
  const [openSection, setOpenSection] = useState<string | null>(null);
  const [inputText, setInputText] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  // Sticking to the newest message is a chat behaviour, not a drawer one. Applying it to every
  // section dropped you at the BOTTOM of the quiz composer the moment you opened it, so anything
  // but chat starts at the top instead.
  useEffect(() => {
    const body = scrollRef.current;
    if (!body) return;
    body.scrollTop = openSection === 'chat' ? body.scrollHeight : 0;
  }, [messages, openSection]);

  const handleSend = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!inputText.trim() || !isConnected) return;
    await sendMessage(inputText);
    setInputText('');
  };

  const sections: SidebarSection[] = [
    {
      id: 'chat',
      label: 'Chat',
      description: 'Message everyone in the session',
      icon: <MessageSquare size={17} />,
    },
    {
      id: 'qa',
      label: 'Q&A',
      description: 'Ask the teacher a question',
      icon: <HelpCircle size={17} />,
    },
    {
      id: 'quiz',
      label: 'Quiz',
      description: isTeacher ? 'Compose, run and mark a quiz' : 'Answer and see your marks',
      icon: <ListChecks size={17} />,
    },
    // The teacher's live student-permission and recording controls; hidden from students.
    ...(isTeacher
      ? [
          {
            id: 'settings',
            label: 'Session Settings',
            description: 'Permissions and recording',
            icon: <SlidersHorizontal size={17} />,
          },
        ]
      : []),
  ];

  const current = sections.find((section) => section.id === openSection) ?? null;

  const renderPanel = () => {
    switch (openSection) {
      case 'quiz':
        if (!classroomId || !sessionId) return null;
        // Teacher composes and runs it; a student only ever gets the answer-key-free view.
        return isTeacher ? (
          <TeacherQuizPanel classroomId={classroomId} sessionId={sessionId} liveEvent={quizEvent} />
        ) : (
          <StudentQuizPanel classroomId={classroomId} sessionId={sessionId} liveEvent={quizEvent} />
        );

      case 'settings':
        if (!isTeacher || !sessionId) return null;
        return (
          <SessionSettingsPanel
            sessionId={sessionId}
            livePolicy={publishPolicy}
            liveRecordingState={recordingState}
          />
        );

      case 'chat':
        return (
          <div className="space-y-5 p-4">
            {messages.length === 0 ? (
              <div className="flex h-40 flex-col items-center justify-center text-slate-700">
                <MessageSquare size={32} className="mb-2 opacity-20" />
                <p className="text-[10px] font-bold uppercase tracking-tighter">No messages yet</p>
              </div>
            ) : (
              messages.map((m, index) => {
                const isMe = m.userId === user?.id;
                return (
                  <div key={index} className={`flex flex-col ${isMe ? 'items-end' : 'items-start'}`}>
                    <span className="mb-1 px-1 text-[9px] font-bold uppercase text-slate-500">
                      {isMe ? 'You' : m.userName}
                    </span>
                    <div
                      className={`max-w-[92%] rounded-2xl p-3 shadow-sm ${
                        isMe
                          ? 'rounded-tr-none bg-violet-600 text-white'
                          : 'rounded-tl-none border border-white/5 bg-slate-800 text-slate-200'
                      }`}
                    >
                      <p className="break-words text-sm leading-tight">{m.message}</p>
                    </div>
                  </div>
                );
              })
            )}
          </div>
        );

      default:
        return (
          <div className="flex h-full flex-col items-center justify-center p-8 text-center">
            <HelpCircle className="mb-4 text-slate-800" size={48} />
            <h3 className="mb-1 text-sm font-bold text-slate-200">Q&amp;A</h3>
            <p className="text-[11px] text-slate-500">This feature is coming soon.</p>
          </div>
        );
    }
  };

  return (
    <aside className="z-20 flex h-full w-80 flex-col overflow-hidden border-l border-white/10 bg-slate-900 shadow-2xl">
      {current ? (
        <>
          <div className="flex flex-shrink-0 items-center gap-2 border-b border-white/5 bg-slate-950/50 p-2">
            <button
              type="button"
              onClick={() => setOpenSection(null)}
              aria-label="Back to sections"
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-slate-400 outline-none transition-colors hover:bg-white/10 hover:text-slate-200 focus-visible:bg-white/10"
            >
              <ArrowLeft size={17} />
            </button>
            <h2 className="min-w-0 truncate text-sm font-bold text-slate-200">{current.label}</h2>
          </div>

          <div
            ref={scrollRef}
            className="custom-scrollbar min-h-0 flex-1 overflow-y-auto bg-slate-900/20"
          >
            {renderPanel()}
          </div>

          {/* Section footers live in the container, so a panel only appears when its section is open. */}
          {openSection === 'chat' && (
            <div className="flex-shrink-0 border-t border-white/5 bg-slate-950 p-4">
              <form onSubmit={handleSend} className="flex gap-2">
                <input
                  value={inputText}
                  onChange={(e) => setInputText(e.target.value)}
                  disabled={!isConnected}
                  placeholder={isConnected ? 'Say something...' : 'Reconnecting...'}
                  className="flex-1 rounded-xl border border-white/10 bg-white/5 px-4 py-2 text-sm text-white outline-none transition-all placeholder:text-slate-600 focus:border-violet-500"
                />
                <Button
                  type="submit"
                  disabled={!isConnected || !inputText.trim()}
                  className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl !p-0"
                >
                  <Send size={18} />
                </Button>
              </form>
            </div>
          )}
        </>
      ) : (
        <div className="custom-scrollbar min-h-0 flex-1 overflow-y-auto">
          <SidebarMenu sections={sections} onOpen={setOpenSection} />
        </div>
      )}
    </aside>
  );
};
