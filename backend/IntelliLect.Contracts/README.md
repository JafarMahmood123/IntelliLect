# IntelliLect.Contracts

The shared message contracts every .NET service publishes and consumes. Packaged as a NuGet package
and resolved from `LocalPackages/`, so services depend on a **versioned artifact** rather than on
each other's source.

---

## Why a package rather than a project reference

A project reference would make every consumer rebuild whenever any producer's internals changed, and
would quietly permit a contract to depend on a service's domain types. A package cannot: it has no
dependencies of its own, so a message can only ever contain primitives and its own records.

It also makes a breaking change visible. Bumping the package version is a deliberate act, and a
service that has not upgraded keeps compiling against the contract it was written for.

```text
backend/LocalPackages/IntelliLect.Contracts.1.3.0.nupkg
```

`nuget.config` adds that directory as a source.

---

## The contracts

| Message | Published by | Consumed by |
| --- | --- | --- |
| `SendTwoFactorCodeMessage` | UserManagement | Email |
| `SendResetCodeMessage` | UserManagement | Email |
| `UserStatusChangedMessage` | UserManagement | Email, Classroom |
| `ClassroomTeacherChangedMessage` | UserManagement | Email, Classroom |
| `ClassroomMembershipChangedMessage` | UserManagement | Email, Classroom |
| `SessionStartedMessage` | Classroom | LiveAssistant trigger path |
| `SessionRecordingReadyMessage` | Streaming | Classroom |
| `SessionSummaryRequestedMessage` | Classroom | LiveAssistant |
| `SessionSummaryReadyMessage` | LiveAssistant | Classroom |

---

## Rules these follow

**Events are facts, not commands.** `SessionRecordingReadyMessage` states that a recording exists;
it does not instruct anyone to do anything. That is what lets a second consumer be added later
without touching the publisher.

**No request/response over the bus.** Every message here is fire-and-forget. When a service needs an
*answer* it uses internal HTTP on `/api/internal` with a shared secret. A service blocking on a
queue for a reply is a distributed deadlock waiting to happen, and it makes the failure mode a
timeout rather than a connection error.

**Primitives only.** No domain entities, no EF Core types, no enums owned by a service. A message
must be deserialisable by a consumer that shares none of the publisher's code.

**Additive changes only within a major version.** Adding an optional field is safe; removing or
renaming one is not, because publisher and consumers deploy independently.

---

## Adding a contract

1. Add the record here.
2. Bump the version in the `.csproj`.
3. `dotnet pack` into `backend/LocalPackages/`.
4. Update the `PackageReference` in each service that needs it.

Step 4 being explicit is the point: a service opts in to a new contract version rather than being
dragged along by a rebuild.
