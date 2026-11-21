import asyncio
from types import SimpleNamespace

from starlette.types import Message, Receive, Scope, Send

from antifraud.admission import CHECKS_PATH, AdmissionLimit


class HeldApp:
    """The app behind the middleware: every request waits until the test lets it go, then answers 200."""

    def __init__(self) -> None:
        self.release = asyncio.Event()
        self.entered = 0

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        self.entered += 1
        await self.release.wait()
        await send({"type": "http.response.start", "status": 200, "headers": []})
        await send({"type": "http.response.body", "body": b"{}"})


def scope(path: str = CHECKS_PATH, max_in_flight: int = 2) -> Scope:
    app = SimpleNamespace(state=SimpleNamespace(settings=SimpleNamespace(max_in_flight=max_in_flight)))
    return {"type": "http", "method": "POST", "path": path, "headers": [], "app": app}


async def call(middleware: AdmissionLimit, request_scope: Scope) -> int:
    """Sends one request through the middleware; returns the status it answered with."""
    messages: list[Message] = []

    async def receive() -> Message:
        return {"type": "http.request", "body": b"", "more_body": False}

    async def send(message: Message) -> None:
        messages.append(message)

    await middleware(request_scope, receive, send)
    return next(m["status"] for m in messages if m["type"] == "http.response.start")


async def test_check_over_the_limit_is_refused_at_once_without_reaching_the_app() -> None:
    held = HeldApp()
    middleware = AdmissionLimit(held)
    taken = [asyncio.create_task(call(middleware, scope(max_in_flight=2))) for _ in range(2)]
    await asyncio.sleep(0)  # both are inside the app now, waiting

    # The third is answered while the first two still wait: no queue behind them.
    assert await asyncio.wait_for(call(middleware, scope(max_in_flight=2)), timeout=1) == 503
    assert held.entered == 2

    held.release.set()
    assert await asyncio.gather(*taken) == [200, 200]


async def test_place_is_given_back_when_a_check_ends() -> None:
    held = HeldApp()
    held.release.set()
    middleware = AdmissionLimit(held)

    for _ in range(5):
        assert await call(middleware, scope(max_in_flight=1)) == 200
    assert middleware.in_flight == 0


async def test_place_is_given_back_when_a_check_fails() -> None:
    async def failing(scope: Scope, receive: Receive, send: Send) -> None:
        raise RuntimeError("boom")

    middleware = AdmissionLimit(failing)
    try:
        await call(middleware, scope(max_in_flight=1))
    except RuntimeError:
        pass
    assert middleware.in_flight == 0


async def test_health_check_answers_when_the_checks_are_full() -> None:
    held = HeldApp()
    middleware = AdmissionLimit(held)
    taken = asyncio.create_task(call(middleware, scope(max_in_flight=1)))
    await asyncio.sleep(0)

    # /healthz goes past the limit: a pod with full checks is busy, not dead.
    health = asyncio.create_task(call(middleware, scope(path="/healthz", max_in_flight=1)))
    await asyncio.sleep(0)
    assert held.entered == 2

    held.release.set()
    assert await asyncio.gather(taken, health) == [200, 200]


async def test_zero_turns_the_limit_off() -> None:
    held = HeldApp()
    middleware = AdmissionLimit(held)
    taken = [asyncio.create_task(call(middleware, scope(max_in_flight=0))) for _ in range(20)]
    await asyncio.sleep(0)

    assert held.entered == 20
    held.release.set()
    assert await asyncio.gather(*taken) == [200] * 20
