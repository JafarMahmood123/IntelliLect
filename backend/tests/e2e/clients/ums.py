"""UserManagementService client: roles, registration, admin approval, login.

All traffic goes through the nginx gateway (`/api/*` that is not `/api/classrooms`
or `/api/streams` routes to user-service).
"""

from __future__ import annotations

from dataclasses import dataclass

import httpx

from clients.http import expect_ok, get_ci


@dataclass
class Account:
    user_id: str
    email: str
    password: str
    role: str
    access_token: str | None = None

    @property
    def auth(self) -> dict[str, str]:
        assert self.access_token, f"{self.role} {self.email} is not logged in"
        return {"Authorization": f"Bearer {self.access_token}"}


class UmsClient:
    def __init__(self, gateway_url: str, timeout_s: float) -> None:
        self._http = httpx.Client(base_url=gateway_url, timeout=timeout_s)

    def close(self) -> None:
        self._http.close()

    # --- discovery -----------------------------------------------------------
    def registration_role_ids(self) -> dict[str, str]:
        """Return {roleName: roleId} for the self-registration roles (Teacher/Student)."""
        resp = expect_ok(self._http.get("/api/auth/registration-roles"))
        return {get_ci(r, "name"): get_ci(r, "id") for r in resp.json()}

    # --- registration + approval --------------------------------------------
    def register(
        self,
        *,
        username: str,
        email: str,
        first_name: str,
        last_name: str,
        role_id: str,
        password: str,
        role_name: str,
    ) -> Account:
        body = {
            "userName": username,
            "email": email,
            "firstName": first_name,
            "lastName": last_name,
            "roleId": role_id,
            "password": password,
        }
        resp = expect_ok(self._http.post("/api/auth/register", json=body))
        user_id = get_ci(resp.json(), "userId")
        assert user_id, f"register did not return a userId: {resp.text}"
        return Account(user_id=user_id, email=email, password=password, role=role_name)

    def approve(self, admin: Account, user_id: str) -> None:
        """Admin flips a Pending account to Active. Body is a raw JSON string."""
        resp = self._http.put(
            f"/api/admin/requests/{user_id}/status",
            content=b'"Active"',
            headers={**admin.auth, "Content-Type": "application/json"},
        )
        expect_ok(resp)

    # --- login ---------------------------------------------------------------
    def login(self, email: str, password: str) -> dict:
        resp = expect_ok(
            self._http.post("/api/auth/login", json={"email": email, "password": password})
        )
        return resp.json()

    def login_raw(self, email: str, password: str) -> httpx.Response:
        """Login without asserting success — for the cases whose point is the refusal."""
        return self._http.post("/api/auth/login", json={"email": email, "password": password})

    def login_account(self, account: Account) -> Account:
        payload = self.login(account.email, account.password)
        token = get_ci(payload, "accessToken")
        assert token, (
            f"login for {account.email} returned no accessToken "
            f"(2FA-gated or not approved?): {payload}"
        )
        account.access_token = token
        return account

    # --- the bulk path (§2, §8.2) -------------------------------------------

    def approve_bulk(self, admin: Account, user_ids: list[str], action: str = "Accept") -> dict:
        """Apply one action to many pending registrations in a single request.

        Always 200 when the request is well-formed; the body reports each account
        separately, because partial success is the expected outcome rather than an error.
        """
        resp = expect_ok(
            self._http.put(
                "/api/admin/requests/status",
                json={"userIds": user_ids, "action": action},
                headers=admin.auth,
            )
        )
        return resp.json()

    def pending_requests(self, admin: Account) -> list[dict]:
        resp = expect_ok(self._http.get("/api/admin/requests", headers=admin.auth))
        payload = resp.json()
        # Paged or bare list, depending on the endpoint's shape.
        return get_ci(payload, "items", payload) if isinstance(payload, dict) else payload

    def refresh_raw(self, refresh_token: str) -> httpx.Response:
        """Renew a session without asserting success — revocation is the interesting case."""
        return self._http.post("/api/auth/refresh", json={"refreshToken": refresh_token})

    def me(self, account: Account) -> dict:
        resp = expect_ok(self._http.get("/api/users/me", headers=account.auth))
        return resp.json()
