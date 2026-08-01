"""Internal quiz-generation endpoint.

Turns the idea a teacher has just finished explaining into multiple-choice questions, for
ClassroomService to store as a Draft the teacher reviews before publishing. Read-only with respect
to this service: nothing here is persisted, because the quiz belongs to ClassroomService — grades
are academic records, and this service holds none.

Secured by ``INTERNAL_API_SECRET`` (``X-Internal-Secret``), the same guard as the other
``/api/internal`` routes. Never called by a browser: the caller is ClassroomService, which has
already established that the requester is the session's teacher.

The bounds arrive in the REQUEST rather than being configured here. ClassroomService owns the quiz
limits, and a second copy in this service's settings would be a copy free to disagree with the one
that actually rejects a publish.
"""

from __future__ import annotations

from uuid import UUID

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field

from app.api.dependencies import QuizGeneratorDep, require_internal_secret
from app.application.services.quiz_generator import NoIdeaAvailable, QuizGenerationFailed

router = APIRouter(
    prefix="/api/internal/sessions",
    tags=["quizzes"],
    dependencies=[Depends(require_internal_secret)],
)


class GenerateQuizRequest(BaseModel):
    """camelCase in, matching the .NET caller's serialisation."""

    classroomId: UUID
    questionCount: int = Field(default=3, ge=1, le=50)
    minOptions: int = Field(default=2, ge=2, le=10)
    maxOptions: int = Field(default=4, ge=2, le=10)
    # Questions already in the teacher's draft, so a newly generated one does not repeat them.
    avoid: list[str] = Field(default_factory=list)


class GenerateAnswersRequest(BaseModel):
    classroomId: UUID
    questionText: str = Field(min_length=1)
    minOptions: int = Field(default=2, ge=2, le=10)
    maxOptions: int = Field(default=4, ge=2, le=10)


class GeneratedOptionResponse(BaseModel):
    text: str
    isCorrect: bool


class CorrectionResponse(BaseModel):
    """A point where the course material contradicted the teacher.

    Travels beside the questions rather than inside them because it is about the LESSON, not any
    one question, and because the teacher must see it before publishing — a quiz whose answer key
    silently disagrees with what they told the room is worse than the slip it corrects.
    """

    taught: str
    corrected: str


class GeneratedQuestionResponse(BaseModel):
    text: str
    options: list[GeneratedOptionResponse]
    corrections: list[CorrectionResponse] = []


class GenerateQuizResponse(BaseModel):
    sessionId: UUID
    title: str
    # False when no course material was relevant and the quiz came from the teacher's words alone.
    # Surfaced so the teacher knows which drafts deserve the closest read before publishing.
    grounded: bool
    citations: list[int]
    corrections: list[CorrectionResponse]
    questions: list[GeneratedQuestionResponse]


@router.post("/{session_id}/quiz", response_model=GenerateQuizResponse)
async def generate_quiz(
    session_id: UUID, request: GenerateQuizRequest, generator: QuizGeneratorDep
) -> GenerateQuizResponse:
    """Generate questions about the teacher's most recent idea.

    409 when the session has not produced an idea yet — nothing is broken, the lecture has simply
    not said enough, and the teacher fixes it by carrying on. 503 when retrieval or the brain could
    not produce a usable quiz. The two are distinguished so the caller can say which happened
    instead of showing one apologetic message for both.
    """
    if request.maxOptions < request.minOptions:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail="maxOptions cannot be smaller than minOptions.",
        )

    try:
        quiz = await generator.generate(
            session_id,
            request.classroomId,
            question_count=request.questionCount,
            min_options=request.minOptions,
            max_options=request.maxOptions,
            avoid=request.avoid,
        )
    except NoIdeaAvailable as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc
    except QuizGenerationFailed as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=str(exc)
        ) from exc

    return GenerateQuizResponse(
        sessionId=session_id,
        title=quiz.title,
        grounded=quiz.grounded,
        citations=quiz.citations,
        corrections=[_to_correction(c) for c in quiz.corrections],
        questions=[_to_response(question) for question in quiz.questions],
    )


@router.post("/{session_id}/quiz/answers", response_model=GeneratedQuestionResponse)
async def generate_answers(
    session_id: UUID, request: GenerateAnswersRequest, generator: QuizGeneratorDep
) -> GeneratedQuestionResponse:
    """Write answer options for a question the teacher wrote themselves.

    Same status contract as generation: 409 when the session has produced no idea yet, 503 when
    the assistant could not produce usable options.
    """
    if request.maxOptions < request.minOptions:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail="maxOptions cannot be smaller than minOptions.",
        )

    try:
        question = await generator.generate_answers(
            session_id,
            request.classroomId,
            request.questionText,
            min_options=request.minOptions,
            max_options=request.maxOptions,
        )
    except NoIdeaAvailable as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc
    except QuizGenerationFailed as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=str(exc)
        ) from exc

    return _to_response(question)


def _to_response(question) -> GeneratedQuestionResponse:
    return GeneratedQuestionResponse(
        text=question.text,
        options=[
            GeneratedOptionResponse(text=option.text, isCorrect=option.is_correct)
            for option in question.options
        ],
        corrections=[_to_correction(c) for c in question.corrections],
    )


def _to_correction(correction) -> CorrectionResponse:
    return CorrectionResponse(taught=correction.taught, corrected=correction.corrected)
