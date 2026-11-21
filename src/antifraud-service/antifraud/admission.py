"""Admission control: a worker process takes at most so many checks at once and refuses the rest at once.

Without it an overloaded process takes every request, and they queue in its event loop. Each still goes to Redis,
but the process gets to the answer later than ANTIFRAUD_REDIS_TIMEOUT_MS: the timeout fires, and the check that has
cost its processor ends in 503 all the same. The more overload, the more processor goes to checks already lost: at
1500 checks a second four processes gave about 70 decisions a second instead of the 1000 they give at 1000
(docs/load-testing.md, "Обвал за пределом мощности").

With the limit, a check over it gets 503 at once, without parsing the body and without Redis: the checks taken stay
within the budget, decisions a second stay near the capacity, and only the excess gets "outcome unknown" — in a
millisecond, not in hundreds. Payments.Api treats this 503 like any other: outcome unknown, one retry.

Why 8 by default: a check costs about 2 ms of processor, so eight checks in flight wait at most about 16 ms behind each
other — within the 20 ms Redis timeout and well within the 50 ms attempt of Payments.Api.
"""

from starlette.responses import JSONResponse
from starlette.types import ASGIApp, Receive, Scope, Send

CHECKS_PATH = "/v1/checks"


class AdmissionLimit:
    """Pure ASGI middleware, not BaseHTTPMiddleware: the refusal must cost as little as possible.

    The limit is read from app.state.settings (main.py sets it at startup), so the tests can set it per app. One counter
    per worker process: the event loop is single-threaded, and nothing awaits between the check and the increment.
    """

    def __init__(self, app: ASGIApp) -> None:
        self.app = app
        self.in_flight = 0

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or scope["path"] != CHECKS_PATH:
            await self.app(scope, receive, send)  # /healthz must answer even when the checks are full
            return

        settings = getattr(scope["app"].state, "settings", None)
        limit = settings.max_in_flight if settings else 0
        if limit and self.in_flight >= limit:
            response = JSONResponse(status_code=503, content={"error": "overloaded"})
            await response(scope, receive, send)
            return

        self.in_flight += 1
        try:
            await self.app(scope, receive, send)
        finally:
            self.in_flight -= 1
