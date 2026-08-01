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


class GeneratedOptionResponse(BaseModel):
    text: str
    isCorrect: bool


class GeneratedQuestionResponse(BaseModel):
    text: str
    options: list[GeneratedOptionResponse]


class GenerateQuizResponse(BaseModel):
    sessionId: UUID
    title: str
    # False when no course material was relevant and the quiz came from the teacher's words alone.
    # Surfaced so the teacher knows which drafts deserve the closest read before publishing.
    grounded: bool
    citations: list[int]
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
        questions=[
            GeneratedQuestionResponse(
                text=question.text,
                options=[
                    GeneratedOptionResponse(text=option.text, isCorrect=option.is_correct)
                    for option in question.options
                ],
            )
            for question in quiz.questions
        ],
    )
