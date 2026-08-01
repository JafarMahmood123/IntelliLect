# UserManagementService

Identity for the platform: registration and approval, authentication, token lifecycle, roles, and
the super-admin console that reaches across every other service.

.NET 10 · Clean Architecture · Postgres · **120 unit tests**

---

## Contents

- [Responsibilities](#responsibilities)
- [Architecture](#architecture)
- [Roles and the approval workflow](#roles-and-the-approval-workflow)
- [Authentication](#authentication)
- [Two-stage super-admin login](#two-stage-super-admin-login)
- [The super-admin console](#the-super-admin-console)
- [API surface](#api-surface)
- [Events published](#events-published)
- [Running](#running)
- [Tests](#tests)

---

## Responsibilities

| Area | What it owns |
| --- | --- |
| **Registration** | Sign-up, the roles a user may self-select, pending-approval state |
| **Authentication** | Password login, JWT issuance, refresh-token rotation, logout |
| **Two-factor** | Email codes, required for super-admin sessions |
| **Password recovery** | Reset codes with expiry, single-use tokens |
| **Roles** | Student, Teacher, Admin, SuperAdmin |
| **Admin console** | Approving registrations, deactivating and reactivating users |
| **Super-admin console** | Platform-wide view of users, classrooms, memberships and live sessions |

It is the **only** service that stores credentials. Everything else trusts the JWT and reads claims
from it; no other service ever queries this database.

---

## Architecture

```text
UserManagementService.Api            composition root, JWT setup, authorization policies
UserManagementService.Presentation   controllers
UserManagementService.Application    services, DTOs, abstractions
UserManagementService.Infrastructure EF Core, hashing, token generation, messaging
UserManagementService.Domain         User, Role, RefreshToken, ResetPasswordToken, TwoFactorChallenge, UserStatus
```

This service is also the **gateway's default upstream**: nginx routes `/api/` here and only carves
out `/api/classrooms` and `/api/streams` for the other two. `/scalar` and `/openapi` are served from
here too.

---

## Roles and the approval workflow

```text
register ──▶ Pending ──approved by Admin──▶ Active ──deactivate──▶ Deactivated
                 │                                        ▲
                 └──rejected──▶ Rejected                  └──reactivate──┘
```

A `Pending` user authenticates successfully but is held at `/pending-approval` in the frontend —
they have an identity but no access. `GET /api/auth/registration-roles` returns the roles a user may
self-select, so the client never has to hard-code that list and privileged roles cannot be requested
at sign-up.

Every status change publishes `UserStatusChangedMessage`, which EmailService consumes to notify the
user and other services consume to react.

---

## Authentication

**Access token** — short-lived JWT carrying the user id, role and status. Every other service
validates it locally against the shared signing key; there is no introspection call, so a service
can authenticate a request without this one being reachable.

**Refresh token** — persisted, rotated on use, revocable. `POST /api/auth/refresh` issues a new pair
and invalidates the old refresh token, so a stolen refresh token is usable at most once before the
legitimate client's next refresh invalidates it.

**Password reset** — a code is emailed via `SendResetCodeMessage`; the corresponding
`ResetPasswordToken` is single-use and expires.

---

## Two-stage super-admin login

A super admin cannot reach the console with a password alone.

```text
POST /api/auth/login       correct password  ──▶  challenge issued, 2FA code emailed
                                                  (the token returned here is NOT enough)
POST /api/auth/verify-2fa  correct code      ──▶  token carrying amr: mfa
                                                  ──▶ SuperAdminTwoFactor policy now passes
```

`AuthorizationPolicies.SuperAdminTwoFactor` requires the **`amr: mfa` claim**, not merely the
SuperAdmin role. A first-stage token has the role but not the claim, so every route on
`SuperAdminController` rejects it. The distinction matters: role alone would make the second stage
decorative.

`TwoFactorChallenge` rows carry an expiry and are consumed on use.

---

## The super-admin console

The only place in the platform with a genuinely cross-cutting view. It reaches other services over
internal HTTP rather than touching their databases:

| Area | Notes |
| --- | --- |
| **Users** | Search, detail, status changes across every role |
| **Classrooms** | Create, edit, reassign the teacher, list members, add/remove students |
| **Deletion impact** | `GET classrooms/{id}/deletion-impact` reports what a delete would destroy — sessions, recordings, files — **before** it is confirmed |
| **Sessions** | Platform-wide list, plus a live view |

`deletion-impact` exists because a classroom delete cascades into another service's data. Showing
the count first turns an irreversible action into an informed one.

Reassigning a teacher publishes `ClassroomTeacherChangedMessage`; membership edits publish
`ClassroomMembershipChangedMessage`.

---

## API surface

### `/api/auth`

```text
POST   register
POST   login                    → token, or a 2FA challenge for super admins
POST   verify-2fa               → token carrying amr: mfa
POST   refresh                  rotates the refresh token
POST   logout                   [Authorize]
POST   forgot-password
POST   reset-password
GET    registration-roles       roles a user may self-select
```

### `/api/users` — `[Authorize]`

```text
GET    me
PUT    me
POST   change-password
```

### `/api/admin` — `[Authorize(Roles = Admin)]`

```text
GET    requests                 pending registrations
PUT    requests/{id}/status     approve or reject
GET    users
PUT    users/{id}/deactivate
PUT    users/{id}/reactivate
```

### `/api/super-admin` — `[Authorize(Policy = SuperAdminTwoFactor)]`

```text
GET    admins | admins/search | users | users/{id}
PUT    users/{id}/status
GET    classrooms
POST   classrooms
PUT    classrooms/{id}
GET    classrooms/{id}/deletion-impact
DELETE classrooms/{id}
PUT    classrooms/{id}/teacher
GET    classrooms/{id}/members
POST   classrooms/{id}/members
DELETE classrooms/{id}/members/{studentId}
GET    sessions | sessions/live
```

---

## Events published

Contracts live in [IntelliLect.Contracts](../IntelliLect.Contracts/).

| Event | Published when | Consumed by |
| --- | --- | --- |
| `SendTwoFactorCodeMessage` | A super admin passes stage one | EmailService |
| `SendResetCodeMessage` | Password reset requested | EmailService |
| `UserStatusChangedMessage` | Approved, rejected, deactivated, reactivated | EmailService, others |
| `ClassroomTeacherChangedMessage` | Super admin reassigns a teacher | EmailService, ClassroomService |
| `ClassroomMembershipChangedMessage` | Super admin edits enrolment | EmailService, ClassroomService |

---

## Running

```bash
cd backend && docker compose up -d user-service
```

Postgres is published on **5432**. Migrations are applied at startup. API docs at `/scalar`.

Key configuration: `Jwt:SecretKey` / `Issuer` / `Audience` — the signing key must match every other
service, since they validate tokens locally.

---

## Tests

```bash
cd backend
dotnet test UserManagementService/tests/UserManagementService.UnitTests/UserManagementService.UnitTests.csproj
```

**120 tests**, no database and no mail server. Coverage concentrates on the parts where a mistake is
a security bug rather than a defect: refresh-token rotation, the two-stage 2FA gate and the `amr`
claim requirement, reset-token expiry and single use, role-based authorization, and the status
transitions that decide whether a user can act at all.

An integration test project exists alongside for database-backed checks.
