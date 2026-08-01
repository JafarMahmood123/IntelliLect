# EmailService

Turns domain events into email. The only service with **no HTTP surface at all** — it is a pure
MassTransit consumer.

.NET 10 · RabbitMQ · SMTP · no database

---

## Why it exists

Sending mail is slow, fails in ways the caller cannot fix, and is never the reason a user's request
should fail. Registering a user must not block on an SMTP handshake, and a bounced approval notice
must not roll back the approval.

So no service sends mail directly. They publish a fact — *this user's status changed* — and this
service decides that a fact deserves an email. The publisher does not know an email exists, which
means adding, removing or changing a notification touches exactly one service.

---

## Architecture

```text
EmailService.Api             host only — no controllers, no routes
EmailService.Infrastructure  consumers, SMTP sender, body factory
EmailService.Application     IEmailSender, IEmailBodyFactory, EmailSubjects
```

There is no `Domain` layer, deliberately: this service has no state and no invariants of its own. It
observes other services' facts and performs one side effect.

`IEmailSender` and `IEmailBodyFactory` are separated so the wording of every message can be tested
without an SMTP server — the body factory is a pure function from event to string.

---

## Consumers

| Consumer | Event | Sends |
| --- | --- | --- |
| `SendTwoFactorCodeConsumer` | `SendTwoFactorCodeMessage` | The 2FA code for a super-admin login |
| `SendResetCodeConsumer` | `SendResetCodeMessage` | A password-reset code |
| `UserStatusChangedConsumer` | `UserStatusChangedMessage` | Approved / rejected / deactivated / reactivated |
| `ClassroomTeacherChangedConsumer` | `ClassroomTeacherChangedMessage` | Notice to the incoming and outgoing teacher |
| `ClassroomMembershipChangedConsumer` | `ClassroomMembershipChangedMessage` | Enrolment added or removed |

Contracts live in [IntelliLect.Contracts](../IntelliLect.Contracts/).

### Consumer definitions

`SendTwoFactorCodeConsumerDefinition` and `SendResetCodeConsumerDefinition` configure retry and
concurrency for the two **time-critical** messages. A 2FA code that arrives after the user has given
up is worse than useless — the retry window is deliberately short, because a code that is retried
for a minute has already expired.

The other three carry no deadline and use the default policy.

---

## Configuration

| Setting | Notes |
| --- | --- |
| `Smtp:Host` / `Port` | Mail server |
| `Smtp:Username` / `Password` | Credentials — never logged |
| `Smtp:From` | Sender address |
| `RabbitMq:Host` / `Username` / `Password` | Must match the rest of the stack |

---

## Running

```bash
cd backend && docker compose up -d email-service
```

There is nothing to curl. Confirm it is working by watching the queues in the RabbitMQ management UI
at **<http://localhost:15672>**, or by triggering a password reset and watching the logs.

Because it holds no state, this service can be restarted at any time. Unconsumed messages wait in
the queue.

---

## Notes for reviewers

This service is small on purpose. The interesting decision is not in the code but in the boundary:
**every notification in the platform is a consumer here, not a call there.** Adding an email means
adding one consumer; it never means editing the service that owns the event.
