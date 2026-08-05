"""The Gemini brain — the model that actually decides whether a teacher said something wrong.

`.env.example` and the compose file both set `BRAIN_PROVIDER=gemini`, so this is the brain the
system runs on, yet the transport half of it was untested: the existing brain tests stub
`_complete` and exercise parsing only. What was missing is everything between the prompt and the
parser — the request that gets built, the generation caps that apply to it, and how a refusal, a
block or an outage is handled.

That half matters more than parsing does, because it fails during a live lecture. The assistant
runs beside a teacher who is mid-sentence in front of a class: a crash here is not a stack trace
somebody reads later, it is the assistant going quiet for the rest of the session.
"""

from __future__ import annotations

from uuid import uuid4

import httpx
import pytest

from app.domain.evaluation.feedback_severity import FeedbackSeverity, severity_of
from app.domain.evaluation.feedback_type import FeedbackType
from app.domain.evaluation.retrieved_chunk import RetrievedChunk
from app.domain.idea.boundary_trigger import BoundaryTrigger
from app.domain.idea.completed_idea import CompletedIdea
from app.infrastructure.brain.gemini_brain_client import (
    GeminiBrainClient,
    GeminiBrainError,
    _extract_text,
    _to_gemini_schema,
)
from app.infrastructure.config.settings import Settings

from tests.support.fake_http import FakeHttp, FakeResponse, Request, reply

API_KEY = "test-gemini-key"


def _settings(**overrides) -> Settings:
    base = dict(
        gemini_api_key=API_KEY,
        gemini_base_url="https://gemini.example/v1beta",
        gemini_model="gemini-flash-lite-latest",
        eval_temperature=0.2,
        eval_max_tokens=512,
        eval_timeout_seconds=60.0,
        quiz_temperature=0.4,
        quiz_max_tokens=2048,
        quiz_timeout_seconds=90.0,
    )
    base.update(overrides)
    return Settings(**base)


def _client(monkeypatch, responder, **overrides):
    http = FakeHttp(responder=responder)
    monkeypatch.setattr(httpx, "AsyncClient", http.client_factory())
    return GeminiBrainClient(_settings(**overrides)), http


def _chunks(n: int = 2) -> list[RetrievedChunk]:
    return [
        RetrievedChunk(f"chunk {i}", 0.9 - i * 0.1, uuid4(), uuid4(), slide=i + 1)
        for i in range(n)
    ]


def _idea(text: str = "the sun orbits the earth") -> CompletedIdea:
    return CompletedIdea(text, 0, 1000, 1, BoundaryTrigger.PAUSE)


# --- the request ---------------------------------------------------------------------------


async def test_the_api_key_travels_in_a_header_and_not_the_url(monkeypatch):
    # A key in the query string is copied into proxy logs and error reports by everything it
    # passes through. This one is a live billed credential.
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.smoke_complete("anything")

    assert http.requests[0].headers["x-goog-api-key"] == API_KEY
    assert API_KEY not in http.requests[0].url


async def test_the_configured_model_is_the_one_called(monkeypatch):
    # The model name is a *-latest alias precisely so it can be changed without a deploy; a
    # hard-coded one would rot into a 404 the next time Google retires a version.
    client, http = _client(monkeypatch, lambda _r: reply("{}"), gemini_model="gemini-3-pro")

    await client.smoke_complete("anything")

    assert http.requests[0].url.endswith("/models/gemini-3-pro:generateContent")


async def test_the_system_prompt_goes_in_system_instruction_not_in_the_conversation(monkeypatch):
    # Folded into `contents` it becomes a user turn the model may argue with, summarise, or
    # quote back — instead of an instruction it follows.
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.evaluate(_idea(), _chunks())

    request = http.requests[0]
    assert "teaching assistant" in request.system_prompt.lower()
    assert request.system_prompt not in request.user_prompt


# --- generation caps -----------------------------------------------------------------------


async def test_evaluation_uses_the_evaluation_caps(monkeypatch):
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.evaluate(_idea(), _chunks())

    config = http.requests[0].generation_config
    assert config["temperature"] == 0.2
    assert config["maxOutputTokens"] == 512


async def test_quiz_generation_gets_its_own_budget_not_the_evaluation_one(monkeypatch):
    """A quiz is several questions with several options each; a suggestion is one paragraph.

    The model this runs on THINKS, and thinking tokens are charged against the same cap — so a
    quiz generated under the 512-token evaluation cap truncates mid-JSON. That does not raise:
    the parser gets a partial object, returns nothing, and the teacher sees the button do
    nothing at all.
    """
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.generate_quiz(
        "an idea", _chunks(), question_count=3, min_options=2, max_options=4
    )

    config = http.requests[0].generation_config
    assert config["maxOutputTokens"] == 2048
    assert config["temperature"] == 0.4


async def test_quiz_generation_gets_the_longer_timeout(monkeypatch):
    # Generating a quiz legitimately takes tens of seconds. Sharing the evaluation timeout would
    # abandon good work just before it arrived.
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.generate_quiz(
        "an idea", _chunks(), question_count=1, min_options=2, max_options=4
    )

    assert http.client_kwargs[0]["timeout"] == 90.0


async def test_evaluation_gets_the_shorter_timeout(monkeypatch):
    # The counterpart: an evaluation that hangs for 90 seconds is feedback about something the
    # class has long since moved past.
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.evaluate(_idea(), _chunks())

    assert http.client_kwargs[0]["timeout"] == 60.0


async def test_writing_answers_for_a_teacher_s_own_question_uses_the_quiz_budget(monkeypatch):
    # The teacher types the question and asks the assistant only for the options. Same shape of
    # work as a quiz, so the same caps and the same constrained decoding — not the evaluation
    # ones, which would truncate a set of options the same way.
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.generate_answers(
        "Why is the sky blue?", "an idea", _chunks(), min_options=2, max_options=4
    )

    request = http.requests[0]
    assert request.generation_config["maxOutputTokens"] == 2048
    assert request.generation_config["responseSchema"]["type"] == "OBJECT"
    assert http.client_kwargs[0]["timeout"] == 90.0
    # The teacher's wording is what the question must be about; a rewrite is not what was asked for.
    assert "Why is the sky blue?" in request.user_prompt


async def test_a_constrained_schema_is_sent_for_quiz_generation(monkeypatch):
    # Constrained decoding is what makes a wrong shape impossible rather than merely detected.
    client, http = _client(monkeypatch, lambda _r: reply("{}"))

    await client.generate_quiz(
        "an idea", _chunks(), question_count=2, min_options=3, max_options=5
    )

    config = http.requests[0].generation_config
    assert config["responseMimeType"] == "application/json"
    assert config["responseSchema"]["type"] == "OBJECT"


# --- the configurable extras ---------------------------------------------------------------


async def test_extra_generation_config_from_the_environment_is_merged_in(monkeypatch):
    # The point of the env hook: attaching topP/topK/stopSequences must not need a code change.
    client, http = _client(
        monkeypatch,
        lambda _r: reply("{}"),
        gemini_generation_config_json='{"topP": 0.8, "topK": 40}',
    )

    await client.evaluate(_idea(), _chunks())

    config = http.requests[0].generation_config
    assert config["topP"] == 0.8
    assert config["topK"] == 40
    assert config["maxOutputTokens"] == 512  # the defaults survive alongside


async def test_the_environment_extras_win_over_the_defaults(monkeypatch):
    # Merged on top, so the escape hatch can actually override — otherwise it can only add.
    client, http = _client(
        monkeypatch,
        lambda _r: reply("{}"),
        gemini_generation_config_json='{"maxOutputTokens": 4096}',
    )

    await client.evaluate(_idea(), _chunks())

    assert http.requests[0].generation_config["maxOutputTokens"] == 4096


@pytest.mark.parametrize(
    "configured",
    ["", "   ", "not json at all", "[1, 2, 3]", '"a string"', "null"],
)
async def test_an_unusable_generation_config_is_ignored_rather_than_fatal(monkeypatch, configured):
    """A typo in an env var must not stop the assistant from starting.

    This is read once at construction, during app startup — raising here takes the whole service
    down over a stray comma, and the setting it guards is an optional tuning knob.
    """
    client, http = _client(
        monkeypatch, lambda _r: reply("{}"), gemini_generation_config_json=configured
    )

    await client.evaluate(_idea(), _chunks())

    assert http.requests[0].generation_config["maxOutputTokens"] == 512


async def test_a_missing_api_key_is_warned_about_at_construction(monkeypatch, caplog):
    # It cannot be fatal — Settings defaults it to "" and the service also supports Ollama — so
    # this log line is the only warning before the first 401, which may be an hour into a class.
    monkeypatch.setattr(httpx, "AsyncClient", FakeHttp(lambda _r: reply("")).client_factory())

    with caplog.at_level("WARNING", logger="liveassistant.brain"):
        GeminiBrainClient(_settings(gemini_api_key=""))

    assert "GEMINI_API_KEY" in caplog.text


# --- the schema dialect --------------------------------------------------------------------


def test_the_schema_type_is_uppercased_all_the_way_down():
    """Gemini's `responseSchema` is an OpenAPI-derived proto whose `type` is an ENUM.

    Proto JSON wants the enum NAME — "OBJECT", not "object" — while the canonical schema stays
    lowercase because that is what standard JSON Schema, and therefore Ollama, uses. Convert only
    the top level and the request is rejected for the nested types, which shows up as every quiz
    generation failing rather than as anything naming the schema.
    """
    converted = _to_gemini_schema(
        {
            "type": "object",
            "properties": {
                "questions": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "options": {"type": "array", "items": {"type": "string"}},
                        },
                    },
                },
            },
        }
    )

    assert converted["type"] == "OBJECT"
    questions = converted["properties"]["questions"]
    assert questions["type"] == "ARRAY"
    assert questions["items"]["type"] == "OBJECT"
    assert questions["items"]["properties"]["options"]["type"] == "ARRAY"
    assert questions["items"]["properties"]["options"]["items"]["type"] == "STRING"


def test_everything_that_is_not_a_type_survives_the_translation():
    # The bounds are the whole point of the schema — they are what stops the model proposing a
    # quiz the server's own limits would reject.
    converted = _to_gemini_schema(
        {
            "type": "array",
            "minItems": 3,
            "maxItems": 3,
            "description": "exactly three",
            "items": {"type": "string"},
        }
    )

    assert converted["minItems"] == 3
    assert converted["maxItems"] == 3
    assert converted["description"] == "exactly three"


# --- reading the reply ---------------------------------------------------------------------


def test_a_blocked_prompt_degrades_to_silence_rather_than_an_error():
    """A safety filter fires and there are no candidates at all.

    Lecture material triggers this more often than it sounds — medicine, history, chemistry. The
    assistant has to fall silent for that idea and carry on with the session; raising would take
    down the loop over one sentence the teacher said.
    """
    assert _extract_text({"promptFeedback": {"blockReason": "SAFETY"}}) == ""
    assert _extract_text({"candidates": []}) == ""
    assert _extract_text({}) == ""


def test_a_candidate_with_no_content_is_silence_too():
    # A truncated or empty candidate — real, and it arrives as a 200.
    assert _extract_text({"candidates": [{}]}) == ""
    assert _extract_text({"candidates": [{"content": {}}]}) == ""
    assert _extract_text({"candidates": [{"content": {"parts": []}}]}) == ""


def test_a_reply_split_across_parts_is_joined_not_truncated():
    # Gemini splits long replies across parts. Taking only the first would cut a suggestion in
    # half — and half a correction is worse than none, because the teacher is told they were
    # wrong without being told about what.
    data = {
        "candidates": [
            {"content": {"parts": [{"text": "the earth "}, {"text": "orbits the sun"}]}}
        ]
    }

    assert _extract_text(data) == "the earth orbits the sun"


async def test_a_blocked_evaluation_produces_no_feedback_rather_than_a_crash(monkeypatch):
    # End to end through the client: an empty reply must reach the parser and come back as
    # "nothing to say", which is the silence bias the whole design leans on.
    client, _ = _client(monkeypatch, lambda _r: FakeResponse(200, {"candidates": []}))

    outcome = await client.evaluate(_idea(), _chunks())

    assert outcome.has_feedback is False


async def test_a_real_evaluation_arrives_as_a_typed_outcome_with_its_span(monkeypatch):
    """The happy path, through the transport rather than around it — and the §3 contract.

    The span is what the UI strikes through in red and replaces in green, so it has to survive
    the whole journey: it is verified against the idea text by the parser, and a span the teacher
    did not actually say is dropped rather than highlighted.
    """
    content = (
        '{"has_feedback": true, "type": "discrepancy", '
        '"suggestion": "The earth orbits the sun [1].", '
        '"incorrect_text": "the sun orbits the earth", '
        '"corrected_text": "the earth orbits the sun", "citations": [1]}'
    )
    chunks = _chunks(2)
    client, _ = _client(monkeypatch, lambda _r: reply(content))

    outcome = await client.evaluate(_idea("the sun orbits the earth"), chunks)

    assert outcome.has_feedback is True
    assert outcome.suggestion.type is FeedbackType.DISCREPANCY
    assert severity_of(outcome.suggestion.type) is FeedbackSeverity.INCORRECT
    assert outcome.suggestion.incorrect_text == "the sun orbits the earth"
    assert outcome.suggestion.corrected_text == "the earth orbits the sun"
    assert outcome.suggestion.sources == [chunks[0]]  # 1-based citation mapping


async def test_a_hedged_evaluation_keeps_its_own_severity(monkeypatch):
    # LIKELY is the category the rename created, and it is amber rather than red precisely
    # because the brain is saying "probably" — flattening it to INCORRECT would tell a teacher
    # mid-lecture that they were definitely wrong on the strength of a hedge.
    content = (
        '{"has_feedback": true, "type": "likely", '
        '"suggestion": "That may not be right [1].", "citations": [1]}'
    )
    client, _ = _client(monkeypatch, lambda _r: reply(content))

    outcome = await client.evaluate(_idea(), _chunks(1))

    assert outcome.suggestion.type is FeedbackType.LIKELY
    assert severity_of(outcome.suggestion.type) is FeedbackSeverity.LIKELY


# --- failure -------------------------------------------------------------------------------


@pytest.mark.parametrize("status", [401, 403])
async def test_a_rejected_key_says_which_key_to_check(monkeypatch, status):
    client, _ = _client(
        monkeypatch, lambda _r: FakeResponse(status, text="API key not valid")
    )

    with pytest.raises(GeminiBrainError) as exc:
        await client.smoke_complete("anything")

    assert "GEMINI_API_KEY" in str(exc.value)


async def test_a_bad_key_reported_as_a_400_is_still_a_key_problem(monkeypatch):
    """Google answers 400, not 401, for some malformed-key cases.

    Without the special case it lands in the generic bucket, and the operator goes looking for a
    bad request in a payload that is fine.
    """
    client, _ = _client(
        monkeypatch,
        lambda _r: FakeResponse(400, text='{"error":{"message":"API_KEY_INVALID"}}'),
    )

    with pytest.raises(GeminiBrainError) as exc:
        await client.smoke_complete("anything")

    assert "GEMINI_API_KEY" in str(exc.value)


async def test_an_ordinary_bad_request_is_not_reported_as_a_key_problem(monkeypatch):
    # The other side of that special case: a schema the API rejects must not send the operator
    # to rotate a key that works.
    client, _ = _client(
        monkeypatch, lambda _r: FakeResponse(400, text="Invalid JSON payload")
    )

    with pytest.raises(GeminiBrainError) as exc:
        await client.smoke_complete("anything")

    assert "GEMINI_API_KEY" not in str(exc.value)
    assert "400" in str(exc.value)


async def test_a_server_error_carries_the_status_and_the_body(monkeypatch):
    client, _ = _client(monkeypatch, lambda _r: FakeResponse(503, text="model overloaded"))

    with pytest.raises(GeminiBrainError) as exc:
        await client.smoke_complete("anything")

    assert "503" in str(exc.value) and "model overloaded" in str(exc.value)


async def test_an_unreachable_api_names_the_endpoint_it_could_not_reach(monkeypatch):
    def _refuse(_request: Request):
        raise httpx.ConnectError("connection refused")

    client, _ = _client(monkeypatch, _refuse)

    with pytest.raises(GeminiBrainError) as exc:
        await client.evaluate(_idea(), _chunks())

    assert "gemini.example" in str(exc.value)


async def test_a_timeout_is_raised_as_a_brain_error_for_the_caller_to_degrade(monkeypatch):
    # The pipeline catches this and carries on with the next idea; a bare TimeoutException would
    # escape the brain-error handling and stop the session.
    def _timeout(_request: Request):
        raise httpx.ReadTimeout("too slow")

    client, _ = _client(monkeypatch, _timeout)

    with pytest.raises(GeminiBrainError):
        await client.evaluate(_idea(), _chunks())
