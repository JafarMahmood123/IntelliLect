"""Shared HTTP helpers for the service clients.

ASP.NET Core serializes with camelCase by default and there is no string-enum
converter configured, so enum-valued fields go over the wire as integers. Responses
are read case-insensitively so the tests do not break on casing differences.
"""

from __future__ import annotations

from typing import Any

import httpx


class ApiError(AssertionError):
    """A service returned an unexpected status. Carries the body for debugging."""

    def __init__(self, method: str, url: str, response: httpx.Response) -> None:
        body = response.text
        if len(body) > 800:
            body = body[:800] + "…"
        super().__init__(
            f"{method} {url} -> {response.status_code} (expected 2xx)\n{body}"
        )
        self.status_code = response.status_code
        self.response = response


def expect_ok(response: httpx.Response) -> httpx.Response:
    """Assert a 2xx response, else raise ApiError with the body."""
    if not response.is_success:
        raise ApiError(response.request.method, str(response.request.url), response)
    return response


def get_ci(data: dict[str, Any], key: str, default: Any = None) -> Any:
    """Case-insensitive dict lookup (camelCase vs PascalCase tolerance)."""
    if key in data:
        return data[key]
    lowered = key.lower()
    for k, v in data.items():
        if k.lower() == lowered:
            return v
    return default
