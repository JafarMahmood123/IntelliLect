// Provisioning for `setup()`.
//
// Everything here runs ONCE, before the load starts, and none of it is measured. That
// separation is the whole design: a script that registers its students inside the VU loop
// measures account creation and admin approval, which are not what any of these scenarios are
// about — and the approval endpoint would become the bottleneck that hides the real one.
//
// The cost is that setup() is slow (registering 50 students is ~150 sequential requests) and
// that a failure here aborts the run with an exception rather than a metric. Both are correct:
// a load run against a half-provisioned classroom produces numbers that look like results.

import {
  adminToken,
  approveBulk,
  createClassroom,
  createSession,
  enroll,
  login,
  register,
  registrationRoleIds,
  startSession,
} from './api.js';
import { config } from './config.js';

/** Collision-proof within a run, and readable in the database afterwards. */
export function unique(prefix) {
  const stamp = Date.now().toString(36);
  const salt = Math.floor(Math.random() * 1e6).toString(36);
  return `${prefix}${stamp}${salt}`;
}

/**
 * Register `count` accounts of one role and leave them Pending.
 *
 * Returns `{ userIds, emails }`. Nothing is approved and nothing is logged in — the bulk
 * scenario wants exactly this state, and the others build on it via `activate`.
 */
export function registerMany(count, roleName, prefix) {
  const roles = registrationRoleIds();
  const roleId = roles[roleName];
  if (!roleId) {
    throw new Error(
      `no registration role named ${roleName}; the endpoint offers ${Object.keys(roles).join(', ')}`,
    );
  }

  const userIds = [];
  const emails = [];
  for (let i = 0; i < count; i += 1) {
    const name = unique(`${prefix}${i}_`);
    const email = `${name}@load.intellilect.test`;
    userIds.push(register({
      username: name,
      email,
      firstName: 'Load',
      lastName: `User${i}`,
      roleId,
    }));
    emails.push(email);
  }
  return { userIds, emails };
}

/**
 * Approve a batch and log every account in, returning bearer tokens.
 *
 * Approval goes through the bulk endpoint rather than one call per account: 50 sequential
 * PUTs is a minute of setup for no benefit, and the bulk path is the one a real cohort is
 * approved through anyway.
 */
export function activate(admin, userIds, emails) {
  const approved = approveBulk(admin, userIds);
  if (approved.status < 200 || approved.status >= 300) {
    throw new Error(`bulk approve failed: HTTP ${approved.status} ${String(approved.body).slice(0, 300)}`);
  }
  return emails.map((email) => login(email, config.userPassword));
}

/**
 * A teacher, a classroom, `studentCount` enrolled students, and (optionally) a live session.
 *
 * The teacher is provisioned the same way as the students — registered and approved — rather
 * than reusing the seeded admin. An admin acting as a teacher would exercise a different
 * authorization path from the one a real class uses, and since §7.2b that path is no longer
 * a formality: every classroom route now resolves membership.
 */
export function provisionClassroom({ studentCount, live = true, label = 'load' }) {
  const admin = adminToken();

  const teacherAccounts = registerMany(1, 'Teacher', `${label}t`);
  const [teacher] = activate(admin, teacherAccounts.userIds, teacherAccounts.emails);

  const classroomId = createClassroom(teacher, `Load ${unique(label)}`);

  const studentAccounts = registerMany(studentCount, 'Student', `${label}s`);
  const students = activate(admin, studentAccounts.userIds, studentAccounts.emails);
  for (const student of students) {
    enroll(student, classroomId);
  }

  const sessionId = createSession(teacher, classroomId, `Load session ${label}`);
  if (live) {
    startSession(teacher, classroomId, sessionId);
  }

  return {
    admin,
    teacher,
    students,
    studentIds: studentAccounts.userIds,
    classroomId,
    sessionId,
  };
}

/**
 * The token for the VU currently executing, spread evenly over the provisioned students.
 *
 * `__VU` is 1-based and can exceed the student count when a scenario is configured with more
 * VUs than accounts. Wrapping is deliberate and worth knowing about when reading results: two
 * VUs sharing an account are two connections for one identity, which is a reconnecting student
 * rather than two students — realistic, but not the same thing as unique arrivals.
 */
export const tokenForVU = (students) => students[(__VU - 1) % students.length];
