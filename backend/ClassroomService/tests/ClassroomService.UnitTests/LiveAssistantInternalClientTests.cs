using System.Net;
using ClassroomService.Application.Exceptions;
using ClassroomService.Infrastructure.Configuration;
using ClassroomService.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClassroomService.UnitTests;

/// <summary>
/// The client ClassroomService uses to reach LiveAssistantService. It had no tests.
///
/// Two things live here that exist nowhere else. The first is the internal secret: these routes
/// carry no user token, so the header is the whole of the authorization, and a request that
/// forgets it is a 401 that surfaces as "the assistant is down". The second is the retry policy,
/// which is deliberately NOT uniform — transcript calls retry because a lost one leaves data
/// behind, and quiz generation does not, because retrying means running the model again while a
/// teacher stands in front of a class waiting.
/// </summary>
public sealed class LiveAssistantInternalClientTests
{
    private const string BaseUrl = "http://live-assistant-service:8080/";
    private const string Secret = "test-internal-secret";

    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ClassroomId = Guid.NewGuid();

    private static LiveAssistantInternalClient CreateClient(
        HttpMessageHandler handler, string secret = Secret, int timeoutSeconds = 10)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var options = Options.Create(new LiveAssistantOptions
        {
            BaseUrl = BaseUrl,
            InternalApiSecret = secret,
            TimeoutSeconds = timeoutSeconds,
            GenerationTimeoutSeconds = 120,
        });
        return new LiveAssistantInternalClient(
            httpClient, options, NullLogger<LiveAssistantInternalClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    /// <summary>A quiz body the client will accept, so tests can vary one thing at a time.</summary>
    private const string ValidQuizBody = """
        {
          "title": "Checkpoint",
          "grounded": true,
          "questions": [
            { "text": "What was said?",
              "options": [ {"text":"Right","isCorrect":true}, {"text":"Wrong","isCorrect":false} ] }
          ]
        }
        """;

    // --- the secret ---------------------------------------------------------------

    [Fact]
    public async Task Every_call_carries_the_internal_secret()
    {
        // These routes sit behind the header and nothing else. One call that forgets it is a 401
        // the caller reads as the assistant being unavailable.
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.OK, """{"segmentCount": 3}"""));
        var client = CreateClient(handler);

        await client.GetTranscriptSegmentCountAsync(SessionId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Secret, request.SecretHeader);
        Assert.Equal($"{BaseUrl}api/internal/sessions/{SessionId}/transcript", request.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task An_unconfigured_secret_sends_no_header_rather_than_an_empty_one()
    {
        // An empty header value is worse than none: it looks configured to whoever reads the
        // request, and the failure moves to the far side where nobody is looking.
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.OK, """{"segmentCount": 0}"""));
        var client = CreateClient(handler, secret: "   ");

        await client.GetTranscriptSegmentCountAsync(SessionId);

        Assert.Null(Assert.Single(handler.Requests).SecretHeader);
    }

    // --- transcript status --------------------------------------------------------

    [Fact]
    public async Task A_session_with_no_transcript_is_null_not_zero()
    {
        // "Never transcribed" and "transcribed, nothing said" are different answers, and the
        // deletion flow branches on which one it got.
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        Assert.Null(await client.GetTranscriptSegmentCountAsync(SessionId));
    }

    [Fact]
    public async Task A_transcript_reports_its_segment_count()
    {
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.OK, """{"segmentCount": 42}"""));
        var client = CreateClient(handler);

        Assert.Equal(42, await client.GetTranscriptSegmentCountAsync(SessionId));
    }

    // --- deletion -----------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_session_transcript_sends_a_delete_to_that_session()
    {
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        await client.DeleteSessionTranscriptAsync(SessionId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"{BaseUrl}api/internal/sessions/{SessionId}/transcript", request.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_transcript_delete_that_never_succeeds_throws_rather_than_reporting_done()
    {
        // 6ب. The transcript is the one copy of what was said in the session, and it lives in
        // another service's database — so if this is swallowed, the session row disappears here
        // and the transcript stays there forever, with nothing left pointing at it.
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteSessionTranscriptAsync(SessionId));
    }

    [Fact]
    public async Task Deleting_a_classroom_s_transcripts_reports_how_many_went()
    {
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.OK, """{"transcriptsDeleted": 7}"""));
        var client = CreateClient(handler);

        Assert.Equal(7, await client.DeleteClassroomTranscriptsAsync(ClassroomId));
        Assert.Equal(
            $"{BaseUrl}api/internal/classrooms/{ClassroomId}/transcripts",
            Assert.Single(handler.Requests).Uri!.AbsoluteUri);
    }

    // --- retries ------------------------------------------------------------------

    [Fact]
    public async Task A_transcript_call_retries_a_server_error_and_succeeds_on_a_later_attempt()
    {
        // The failure this covers is a restart on the far side, which is routine — and losing a
        // transcript delete to one is not.
        var attempts = 0;
        var handler = new CapturingHttpMessageHandler(() =>
        {
            attempts++;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = CreateClient(handler);

        await client.DeleteSessionTranscriptAsync(SessionId);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_transcript_call_gives_up_after_three_attempts()
    {
        var attempts = 0;
        var handler = new CapturingHttpMessageHandler(() =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteSessionTranscriptAsync(SessionId));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        // A 401 or a 400 will fail identically every time; retrying only delays the report.
        var attempts = 0;
        var handler = new CapturingHttpMessageHandler(() =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteSessionTranscriptAsync(SessionId));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Quiz_generation_is_never_retried()
    {
        // The deliberate exception to the policy above, and the one an ordinary "make it retry
        // like the others" edit would undo. A retry re-runs the language model: another minute of
        // a teacher standing in front of a class, and another call's cost, to repeat work that
        // just failed. It is reported instead, and the teacher decides whether to press again.
        var attempts = 0;
        var handler = new CapturingHttpMessageHandler(() =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.GenerateQuizAsync(SessionId, ClassroomId, 3, 2, 4));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Answer_generation_is_never_retried_either()
    {
        var attempts = 0;
        var handler = new CapturingHttpMessageHandler(() =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.GenerateAnswersAsync(SessionId, ClassroomId, "What was said?", 2, 4));

        Assert.Equal(1, attempts);
    }

    // --- generation ---------------------------------------------------------------

    [Fact]
    public async Task Generating_a_quiz_sends_the_bounds_the_service_will_enforce()
    {
        // The point of sending them is that the assistant cannot propose a quiz this service would
        // then refuse to publish — the teacher would be shown questions they cannot use.
        var handler = new CapturingHttpMessageHandler(() => Json(HttpStatusCode.OK, ValidQuizBody));
        var client = CreateClient(handler);

        await client.GenerateQuizAsync(
            SessionId, ClassroomId, questionCount: 5, minOptions: 2, maxOptions: 4,
            avoid: ["an earlier question"], wholeSession: true);

        var body = Assert.Single(handler.Requests).Body!;
        Assert.Contains($"\"classroomId\":\"{ClassroomId}\"", body);
        Assert.Contains("\"questionCount\":5", body);
        Assert.Contains("\"minOptions\":2", body);
        Assert.Contains("\"maxOptions\":4", body);
        Assert.Contains("\"avoid\":[\"an earlier question\"]", body);
        Assert.Contains("\"wholeSession\":true", body);
    }

    [Fact]
    public async Task A_generated_quiz_comes_back_with_its_questions_and_answer_key()
    {
        var handler = new CapturingHttpMessageHandler(() => Json(HttpStatusCode.OK, ValidQuizBody));
        var client = CreateClient(handler);

        var quiz = await client.GenerateQuizAsync(SessionId, ClassroomId, 1, 2, 4);

        Assert.Equal("Checkpoint", quiz.Title);
        Assert.True(quiz.Grounded);
        var question = Assert.Single(quiz.Questions);
        Assert.Equal("What was said?", question.Text);
        Assert.Equal(2, question.Options.Count);
        Assert.Single(question.Options, o => o.IsCorrect);
    }

    [Fact]
    public async Task A_quiz_with_no_questions_is_a_failure_not_an_empty_quiz()
    {
        // An empty quiz would open the composer on nothing and read as the button not working.
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.OK, """{"title":"Empty","grounded":true,"questions":[]}"""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.GenerateQuizAsync(SessionId, ClassroomId, 1, 2, 4));
    }

    [Fact]
    public async Task Nothing_to_build_from_is_a_conflict_and_keeps_the_assistant_s_wording()
    {
        // 409 is not a fault: nothing is broken and a retry now fails the same way. The
        // assistant's own sentence is preferred because it separates two situations needing
        // different actions — "nothing transcribed yet" (keep talking) and "everything since has
        // already been quizzed" (talk about something new).
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.Conflict,
                """{"detail":"Everything said since the last quiz has already been covered."}"""));
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ConflictException>(
            () => client.GenerateQuizAsync(SessionId, ClassroomId, 1, 2, 4));

        Assert.Equal("Everything said since the last quiz has already been covered.", error.Message);
    }

    [Fact]
    public async Task A_conflict_with_an_unreadable_body_still_reads_as_a_sentence()
    {
        // Borrowing the assistant's wording must not mean depending on it. A body that is not a
        // problem detail would otherwise turn a handled conflict into an unhandled exception.
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.Conflict, "not json at all"));
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ConflictException>(
            () => client.GenerateQuizAsync(SessionId, ClassroomId, 1, 2, 4));

        Assert.Contains("nothing to build a quiz from", error.Message);
    }

    [Fact]
    public async Task A_failure_is_reported_to_the_teacher_in_words_not_in_a_status_code()
    {
        var handler = new CapturingHttpMessageHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.GenerateQuizAsync(SessionId, ClassroomId, 1, 2, 4));

        Assert.Contains("try again", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("500", error.Message);
    }

    // --- corrections --------------------------------------------------------------

    [Fact]
    public async Task An_incomplete_correction_is_dropped_rather_than_shown_as_half_a_sentence()
    {
        // "You said X" against a blank, or "the material says Y" with nothing to compare it to,
        // is worse than saying nothing — the teacher is told they were wrong without being told
        // about what.
        var handler = new CapturingHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            {
              "title": "Checkpoint", "grounded": false,
              "questions": [ { "text": "Q", "options": [ {"text":"A","isCorrect":true} ] } ],
              "corrections": [
                { "taught": "  the sun orbits the earth  ", "corrected": "  the earth orbits the sun  " },
                { "taught": "something", "corrected": "   " },
                { "taught": null, "corrected": "a correction with nothing to correct" }
              ]
            }
            """));
        var client = CreateClient(handler);

        var quiz = await client.GenerateQuizAsync(SessionId, ClassroomId, 1, 2, 4);

        var correction = Assert.Single(quiz.Corrections);
        // And the surviving one is trimmed, because the model's whitespace ends up on screen.
        Assert.Equal("the sun orbits the earth", correction.Taught);
        Assert.Equal("the earth orbits the sun", correction.Corrected);
    }

    // --- answers for a teacher's own question -------------------------------------

    [Fact]
    public async Task Generating_answers_sends_the_teacher_s_question_text()
    {
        var handler = new CapturingHttpMessageHandler(() => Json(HttpStatusCode.OK, """
            { "text": "ignored", "options": [ {"text":"Right","isCorrect":true},
                                              {"text":"Wrong","isCorrect":false} ] }
            """));
        var client = CreateClient(handler);

        await client.GenerateAnswersAsync(SessionId, ClassroomId, "Why is the sky blue?", 2, 4);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{BaseUrl}api/internal/sessions/{SessionId}/quiz/answers", request.Uri!.AbsoluteUri);
        Assert.Contains("\"questionText\":\"Why is the sky blue?\"", request.Body!);
    }

    [Fact]
    public async Task Answers_with_no_options_are_a_failure_not_an_unanswerable_question()
    {
        var handler = new CapturingHttpMessageHandler(
            () => Json(HttpStatusCode.OK, """{"text":"Q","options":[]}"""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.GenerateAnswersAsync(SessionId, ClassroomId, "Q", 2, 4));
    }
}
