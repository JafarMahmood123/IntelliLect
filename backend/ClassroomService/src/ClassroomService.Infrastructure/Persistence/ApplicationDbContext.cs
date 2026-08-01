using Microsoft.EntityFrameworkCore;
using ClassroomService.Domain.Entities;
using ClassroomService.Domain.Enums;
using MassTransit;

namespace ClassroomService.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Classroom> Classrooms => Set<Classroom>();
    public DbSet<ClassroomFile> ClassroomFiles => Set<ClassroomFile>();
    public DbSet<ClassroomMembership> ClassroomMemberships => Set<ClassroomMembership>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionRecording> SessionRecordings => Set<SessionRecording>();
    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();
    public DbSet<QuizAnswerOption> QuizAnswerOptions => Set<QuizAnswerOption>();
    public DbSet<QuizAnswer> QuizAnswers => Set<QuizAnswer>();
    public DbSet<QuizSubmission> QuizSubmissions => Set<QuizSubmission>();
    public DbSet<QuizExtension> QuizExtensions => Set<QuizExtension>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddTransactionalOutboxEntities();

        // Optimistic concurrency for classrooms (use-case alternate path 6أ): map Postgres'
        // system column "xmin" as a concurrency token. It is maintained by Postgres on every
        // row update and needs no schema change, so a stale super-admin edit is rejected with
        // a DbUpdateConcurrencyException instead of silently overwriting a concurrent change.
        modelBuilder.Entity<Classroom>()
            .Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Deletion lifecycle (this use-case). Stored as int; defaults to Active so existing rows
        // and new classrooms are usable. Indexed because every teacher/student read path filters
        // it out to hide a classroom that is being deleted.
        modelBuilder.Entity<Classroom>(classroom =>
        {
            classroom.Property(c => c.Status)
                .HasConversion<int>()
                .HasDefaultValue(ClassroomStatus.Active);
            classroom.HasIndex(c => c.Status);
        });

        // Sessions carry a denormalized ClassroomId but have NO foreign key to Classroom (the FK was
        // dropped in AddSessionLifecycleTimestamps), so deleting a classroom does not cascade to its
        // sessions — the deletion service removes them explicitly by ClassroomId. Index that column
        // so both the classroom-scoped purge and the impact report are index scans, not seq scans.
        modelBuilder.Entity<Session>()
            .HasIndex(s => s.ClassroomId);

        // Session recordings (R-1). Looked up by session on the recording-ready path and listed
        // by classroom (R-2), so both are indexed. EgressId is the LiveKit correlation id.
        modelBuilder.Entity<SessionRecording>(recording =>
        {
            recording.HasKey(r => r.Id);
            recording.Property(r => r.EgressId).IsRequired();
            // One recording per session: unique so a racing insert can't create a duplicate row.
            recording.HasIndex(r => r.SessionId).IsUnique();
            recording.HasIndex(r => r.ClassroomId);
            recording.HasOne(r => r.Session)
                .WithMany()
                .HasForeignKey(r => r.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Session summaries (S-4). Looked up by session on the summary-ready path and listed by
        // classroom, so both are indexed. Mirrors the SessionRecording configuration.
        modelBuilder.Entity<SessionSummary>(summary =>
        {
            summary.HasKey(s => s.Id);
            // One summary per session: unique so a racing insert can't create a duplicate row.
            summary.HasIndex(s => s.SessionId).IsUnique();
            summary.HasIndex(s => s.ClassroomId);
            summary.HasOne(s => s.Session)
                .WithMany()
                .HasForeignKey(s => s.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // In-session quizzes. Unlike recordings and summaries there are MANY per session, so
        // SessionId is a plain index rather than unique. ClassroomId is indexed for the same reason
        // as above: the classroom-wide read and delete paths filter on it.
        // Quiz ids are assigned in application code (Guid.NewGuid() in QuizService), never by the
        // database. Saying so matters for more than tidiness: when EF meets an untracked entity
        // through a navigation on an already-TRACKED parent, it decides Added vs Modified by
        // asking whether the key is set. Left as store-generated, a key the code had already
        // filled in read as "this row exists", so replacing a draft's questions issued
        // UPDATE ... WHERE Id = <a brand-new Guid>, matched nothing, and failed the request with
        // a DbUpdateConcurrencyException. Creating a quiz was unaffected, because AddAsync marks
        // the whole graph Added regardless — which is why only the edit path broke.
        modelBuilder.Entity<Quiz>(quiz =>
        {
            quiz.HasKey(q => q.Id);
            quiz.Property(q => q.Id).ValueGeneratedNever();
            quiz.Property(q => q.Title).HasMaxLength(200);
            quiz.HasIndex(q => q.SessionId);
            quiz.HasIndex(q => q.ClassroomId);
            quiz.HasOne<Session>()
                .WithMany()
                .HasForeignKey(q => q.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuizQuestion>(question =>
        {
            question.HasKey(q => q.Id);
            question.Property(q => q.Id).ValueGeneratedNever();
            question.Property(q => q.Text).IsRequired().HasMaxLength(1000);
            question.HasOne(q => q.Quiz)
                .WithMany(q => q.Questions)
                .HasForeignKey(q => q.QuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuizAnswerOption>(option =>
        {
            option.HasKey(o => o.Id);
            option.Property(o => o.Id).ValueGeneratedNever();
            option.Property(o => o.Text).IsRequired().HasMaxLength(500);
            option.HasOne(o => o.Question)
                .WithMany(q => q.Options)
                .HasForeignKey(o => o.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuizAnswer>(answer =>
        {
            answer.HasKey(a => a.Id);
            answer.Property(a => a.Id).ValueGeneratedNever();
            // THE integrity rule of this feature: one answer per student per question, arbitrated
            // by the database. An application-level "have they answered?" read is a race — two
            // concurrent submits both pass it and both insert. Changing an answer before the quiz
            // closes is an update to this row, never a second one.
            answer.HasIndex(a => new { a.QuestionId, a.StudentId }).IsUnique();
            // Scoring reads every answer for a quiz, so this is the hot path.
            answer.HasIndex(a => a.QuizId);
            // Per-student rollups across sessions (the deferred summary work) start here.
            answer.HasIndex(a => a.StudentId);
            answer.HasOne(a => a.Question)
                .WithMany()
                .HasForeignKey(a => a.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // A student declaring they have finished, so they need not sit out the timer.
        modelBuilder.Entity<QuizSubmission>(submission =>
        {
            submission.HasKey(s => s.Id);
            submission.Property(s => s.Id).ValueGeneratedNever();
            // One per student per quiz, arbitrated by the database for the same reason as the
            // answer index: two concurrent clicks both pass an application-level check.
            submission.HasIndex(s => new { s.QuizId, s.StudentId }).IsUnique();
            submission.HasOne(s => s.Quiz)
                .WithMany()
                .HasForeignKey(s => s.QuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Extra time for ONE student. Extending the whole class moves Quiz.ClosesAtUtc instead and
        // writes nothing here.
        modelBuilder.Entity<QuizExtension>(extension =>
        {
            extension.HasKey(e => e.Id);
            extension.Property(e => e.Id).ValueGeneratedNever();
            // One row per student per quiz; a second grant updates it rather than stacking.
            extension.HasIndex(e => new { e.QuizId, e.StudentId }).IsUnique();
            extension.HasOne(e => e.Quiz)
                .WithMany()
                .HasForeignKey(e => e.QuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}