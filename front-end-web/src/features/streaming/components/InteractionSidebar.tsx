import { useState, useRef, useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import {
  ArrowLeft,
  HelpCircle,
  ListChecks,
  MessageSquare,
  Send,
  SlidersHorizontal,
  Bell,
  BellOff,
} from 'lucide-react';
import { useStreamHub } from '../hooks/useStreamHub';
import { useSessionNotifications } from '../hooks/useSessionNotifications';
import { Button } from '../../../components/ui/Button';
import { useAuthStore } from '../../../store/useAuthStore';
import { SessionSettingsPanel } from './SessionSettingsPanel';
import { SidebarMenu, type SidebarSection } from './SidebarMenu';
import { TeacherQuizPanel } from '../../quizzes/components/TeacherQuizPanel';
import { StudentQuizPanel } from '../../quizzes/components/StudentQuizPanel';
import { quizKeys, useOpenQuiz } from '../../quizzes/hooks/useQuizQueries';
import { useToast } from '../../../components/ui/ToastProvider';

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
  const queryClient = useQueryClient();
  const { showToast } = useToast();

  /** `null` is the menu. Opening a section replaces the whole drawer body with it. */
  const [openSection, setOpenSection] = useState<string | null>(null);

  // Asked for HERE, not only inside the quiz panel. The panel is unmounted while the drawer is on
  // its menu or another section, so it cannot notice a quiz starting — which is exactly when a
  // student most needs to be told. Reading it at the container also covers the student who joins
  // or rejoins mid-quiz: the answer arrives from the server, not from a broadcast they missed.
  const { data: openQuiz } = useOpenQuiz(
    isTeacher ? '' : (classroomId ?? ''),
    isTeacher ? '' : (sessionId ?? ''),
  );

  // The broadcast carries an id and a state, never the quiz, so this refetches rather than trusting
  // the wire — the same rule the panels follow, and what keeps the answer key off the socket.
  useEffect(() => {
    if (!quizEvent || !sessionId) return;
    queryClient.invalidateQueries({ queryKey: quizKeys.openForSession(sessionId) });
  }, [quizEvent, queryClient, sessionId]);

  // Unread counts, the tab title and desktop alerts. Owned here rather than inside the panels: a
  // panel that is not the open section is not mounted, so it cannot notice what it missed — which
  // is precisely the case this feature exists for.
  const notifications = useSessionNotifications({
    messages,
    currentUserId: user?.id,
    isChatOpen: openSection === 'chat',
    // A teacher publishes the quiz; being told it started would be telling them their own news.
    openQuiz: !isTeacher && openQuiz ? { id: openQuiz.id, title: openQuiz.title } : null,
    isQuizOpen: openSection === 'quiz',
  });

  // Announced once per quiz. A student on the chat panel would otherwise find out only by opening
  // the drawer's quiz section on a hunch. The toast is the in-app half; the hook raises the
  // desktop half for whoever is not looking at the tab at all.
  const announcedQuizId = useRef<string | null>(null);
  useEffect(() => {
    if (isTeacher || !openQuiz || announcedQuizId.current === openQuiz.id) return;
    announcedQuizId.current = openQuiz.id;
    if (notifications.muted) return;
    showToast({
      type: 'info',
      title: 'Quiz started',
      message: `${openQuiz.title || 'A quiz'} is open. Open the Quiz panel to answer it.`,
    });
  }, [openQuiz, isTeacher, showToast, notifications.muted]);

  // Cleared as soon as they open it — a badge that outstays what it announced is noise.
  const quizWaiting = Boolean(openQuiz) && !isTeacher && openSection !== 'quiz';

  const openPanel = (id: string) => {
    if (id === 'chat') notifications.markChatRead();
    setOpenSection(id);
  };

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
      badge: notifications.unreadChat > 0 ? `${notifications.unreadChat}` : undefined,
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
      badge: quizWaiting ? 'Live' : undefined,
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
    // Widens with the viewport rather than taking a fixed bite out of it. At 320px the quiz
    // composer's answer fields cut text off mid-word, which is the one panel where the exact
    // wording matters — a teacher is proof-reading what the class is about to be asked.
    //
    // shrink-0 because a fixed width is not a guaranteed one: the aside is a flex child, so
    // without it a long unbroken string in a message could squeeze the whole drawer narrower.
    <aside className="z-20 flex h-full w-80 shrink-0 flex-col overflow-hidden border-l border-white/10 bg-slate-900 shadow-2xl xl:w-96 2xl:w-[28rem]">
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
          <SidebarMenu sections={sections} onOpen={openPanel} />

          <div className="flex items-center justify-between gap-2 border-t border-white/5 px-3 py-2">
            <button
              type="button"
              onClick={notifications.toggleMuted}
              aria-pressed={notifications.muted}
              className="flex items-center gap-1.5 rounded-lg px-2 py-1.5 text-[11px] font-semibold text-slate-400 transition-colors hover:bg-white/10 hover:text-slate-200"
            >
              {notifications.muted ? <BellOff size={14} /> : <Bell size={14} />}
              {notifications.muted ? 'Alerts muted' : 'Alerts on'}
            </button>

            {/* Offered, never taken. A permission dialog nobody asked for on entering a class is
                how a site gets blocked for good — and a blocked site cannot alert anyone about
                anything. Once the answer is in, granted or refused, the offer goes away. */}
            {notifications.permission === 'default' && (
              <button
                type="button"
                onClick={notifications.requestDesktop}
                className="rounded-lg px-2 py-1.5 text-[11px] font-semibold text-violet-300 transition-colors hover:bg-violet-500/15"
              >
                Alert me outside the tab
              </button>
            )}
          </div>
        </div>
      )}
    </aside>
  );
};
