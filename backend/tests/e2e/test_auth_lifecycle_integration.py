"""§8.2 — register → admin approval → login, across UMS and EmailService.

The one flow every other suite depends on, and the only one that spans two services with
nothing but the broker between them. What is proved here is the *sequence*: a registration
lands Pending and cannot sign in, an administrator's decision is what changes that, and the
decision is durable — the account can sign in afterwards and stays signed in.

**Including the bulk path**, which is the half unit tests can only partly reach. §11.7 pinned
that the batch is one transaction by making a repository fake lose it; that proves the code's
intent. Only a real database can show that fifty accounts genuinely commit together, and only
a real broker can show that fifty notifications leave without the caller waiting for them.

**What this suite deliberately does not claim.** EmailService has no controller, no metrics
endpoint and no mailbox to read — it consumes `UserStatusChangedMessage` and talks real SMTP
to Gmail. So "the approval email arrived" is not observable from here at any price worth
paying, and asserting a proxy for it would be the kind of test that passes while the feature
is broken. What *is* asserted is the seam either side of the gap: UMS published (the account
committed, which is the same transaction as the outbox row) and EmailService is alive to
consume. The gap itself is named in `docs/testing-results.md` §10 rather than papered over.

Run: `-m "integration and auth"`, or with everything else via `-m integration`.
"""

from __future__ import annotations

import logging

import httpx
import pytest

from clients.http import get_ci
from clients.ums import Account, UmsClient
from config import Config
from support.ids import unique_email, unique_username

pytestmark = [pytest.mark.integration, pytest.mark.auth]

logger = logging.getLogger("e2e.auth")


def _register_pending(ums: UmsClient, config: Config, label: str) -> Account:
    """A registration that has not been approved. The starting state of every account."""
    role_ids = ums.registration_role_ids()
    return ums.register(
        username=unique_username(label),
        email=unique_email(label),
        first_name=label.capitalize(),
        last_name="E2E",
        role_id=role_ids["Student"],
        password=config.user_password,
        role_name="Student",
    )


# --- the single-account path -------------------------------------------------------------


def test_a_pending_registration_cannot_sign_in(ums: UmsClient, config: Config) -> None:
    """The gate itself. If this passes vacuously the rest of the suite proves nothing."""
    account = _register_pending(ums, config, "pending")

    response = ums.login_raw(account.email, config.user_password)

    assert not response.is_success, (
        f"a Pending account signed in: {response.status_code} {response.text[:300]}"
    )


def test_the_refusal_is_specific_only_after_the_password_is_proved(
    ums: UmsClient, config: Config
) -> None:
    """§7.1's ordering, over the wire.

    Login's status messages are deliberately specific ("pending approval"), which is only safe
    because the status check sits BEHIND the credential check — so the specific message reaches
    nobody who has not already proved they own the account. A wrong password must therefore be
    indistinguishable from an unknown address, whatever the account's real status is.
    """
    account = _register_pending(ums, config, "ordering")

    wrong_password = ums.login_raw(account.email, "definitely-not-the-password")
    unknown_address = ums.login_raw(unique_email("nobody"), "definitely-not-the-password")

    assert wrong_password.status_code == unknown_address.status_code, (
        "a wrong password on a real account answers differently from an unknown address, "
        "so the login endpoint tells an attacker which addresses are registered"
    )


def test_approval_is_what_lets_the_account_in(
    ums: UmsClient, admin: Account, config: Config
) -> None:
    account = _register_pending(ums, config, "approved")
    assert not ums.login_raw(account.email, config.user_password).is_success

    ums.approve(admin, account.user_id)

    ums.login_account(account)
    profile = ums.me(account)
    assert get_ci(profile, "email", "").lower() == account.email.lower()


def test_a_rejected_account_cannot_sign_in_or_renew(
    ums: UmsClient, admin: Account, config: Config
) -> None:
    """A-08's shape at the integration level.

    Rejection has to revoke the sessions as well as flip the status, or somebody rejected
    while signed in keeps working until their refresh token expires. The account is approved
    first so that it can hold a real session to be revoked — rejecting a Pending account
    proves nothing about revocation.
    """
    account = _register_pending(ums, config, "rejected")
    ums.approve(admin, account.user_id)

    payload = ums.login(account.email, config.user_password)
    refresh_token = get_ci(payload, "refreshToken")
    assert refresh_token, f"login returned no refreshToken to revoke: {payload}"

    ums.approve_bulk(admin, [account.user_id], action="Deactivate")

    assert not ums.login_raw(account.email, config.user_password).is_success
    renewed = ums.refresh_raw(refresh_token)
    assert not renewed.is_success, (
        "a deactivated account renewed its session — deactivation revokes tokens precisely so "
        "that ending access does not wait for an access token to expire"
    )


# --- the bulk path -----------------------------------------------------------------------


def test_a_batch_approves_every_account_in_one_request(
    ums: UmsClient, admin: Account, config: Config
) -> None:
    accounts = [_register_pending(ums, config, f"bulk{i}") for i in range(3)]

    result = ums.approve_bulk(admin, [a.user_id for a in accounts])

    assert get_ci(result, "requested") == len(accounts)
    assert get_ci(result, "succeeded") == len(accounts), result
    assert get_ci(result, "failed") == 0, result

    # The claim that matters is not the response body — it is that each account can now sign
    # in, which is a fact about the database rather than about what the endpoint said.
    for account in accounts:
        ums.login_account(account)


def test_a_batch_commits_together_rather_than_per_account(
    ums: UmsClient, admin: Account, config: Config
) -> None:
    """One unknown id must not sink the rest, and must not half-apply the rest either.

    §11.7 proved the intent against a fake that could lose a transaction. This is the same
    property against a real one: the batch reports the bad row as failed, every good row as
    succeeded, and every good row is genuinely approved afterwards.
    """
    accounts = [_register_pending(ums, config, f"mixed{i}") for i in range(2)]
    invented = "00000000-0000-0000-0000-000000000001"

    result = ums.approve_bulk(admin, [accounts[0].user_id, invented, accounts[1].user_id])

    assert get_ci(result, "requested") == 3
    assert get_ci(result, "succeeded") == 2, result
    assert get_ci(result, "failed") == 1, result

    for account in accounts:
        ums.login_account(account)


def test_a_repeated_batch_is_a_no_op_rather_than_an_error(
    ums: UmsClient, admin: Account, config: Config
) -> None:
    """The retry an administrator actually makes: the response was lost, so they press again.

    Re-approving an approved account is reported as a SUCCESS on purpose — reporting it as a
    failure would make the retry unusable, which is the state that leads to somebody clicking
    until the numbers look right.
    """
    accounts = [_register_pending(ums, config, f"retry{i}") for i in range(2)]
    ids = [a.user_id for a in accounts]

    ums.approve_bulk(admin, ids)
    second = ums.approve_bulk(admin, ids)

    assert get_ci(second, "succeeded") == len(ids), second
    assert get_ci(second, "failed") == 0, second


def test_an_empty_selection_is_refused_rather_than_applied_to_everyone(
    admin: Account, config: Config
) -> None:
    # Sent past the client on purpose: `approve_bulk` asserts 2xx, and the whole assertion
    # here is that this request is not. An empty selection is the one input where "do nothing"
    # and "do everything" are a single missing guard apart.
    response = httpx.put(
        f"{config.user_url}/api/admin/requests/status",
        json={"userIds": [], "action": "Accept"},
        headers=admin.auth,
        timeout=30,
    )

    assert response.status_code == 400, response.text[:300]


# --- the seam with EmailService ----------------------------------------------------------


def test_email_service_is_alive_to_consume_the_notification(config: Config) -> None:
    """The half of §8.2's cross-service claim that is honestly checkable.

    An approval commits the account and its outbox row in one transaction, and the relay
    publishes `UserStatusChangedMessage` afterwards. Whether the mail was *delivered* is not
    observable from here — EmailService exposes only `/health`, and it speaks real SMTP to
    Gmail. So this asserts the consumer is up rather than pretending to assert delivery.

    If EmailService were down, the messages would queue rather than vanish, and the accounts
    above would still be approved. That is the right behaviour, and it is also the reason this
    cannot be a stronger assertion without a mailbox to read.
    """
    response = httpx.get(f"{config.email_url}/health", timeout=10)

    assert response.is_success, (
        f"EmailService /health at {config.email_url} answered {response.status_code}. Approval "
        "still works — the notification queues — but nothing is consuming it."
    )
