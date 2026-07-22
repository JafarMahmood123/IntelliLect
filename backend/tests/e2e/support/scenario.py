"""Provisioning helper: the 'a teacher and their enrolled students' starting point."""

from __future__ import annotations

from dataclasses import dataclass, field

from clients.classroom import ClassroomClient
from clients.ums import Account


@dataclass
class TeachingContext:
    teacher: Account
    students: list[Account] = field(default_factory=list)
    classroom_id: str = ""


def provision_classroom(
    make_user,
    classroom: ClassroomClient,
    *,
    student_count: int,
    classroom_name: str,
) -> TeachingContext:
    """Register+approve+login a teacher and N students, create a classroom, enroll all."""
    teacher = make_user("Teacher", "teacher")
    students = [make_user("Student", f"student{i + 1}") for i in range(student_count)]

    classroom_id = classroom.create_classroom(
        teacher, name=classroom_name, description="IntelliLect E2E scenario classroom."
    )
    for student in students:
        classroom.enroll(student, classroom_id)

    return TeachingContext(teacher=teacher, students=students, classroom_id=classroom_id)
