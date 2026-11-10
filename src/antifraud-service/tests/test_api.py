import socket
import time
from collections.abc import Iterator

import pytest
from fakeredis import FakeAsyncRedis, FakeServer
from fastapi.testclient import TestClient

from antifraud.main import app, get_redis

REQUEST = {
    "paymentId": "0192f0c8-0000-7000-8000-000000000001",
    "merchantId": 42,
    "amount": 150000,
    "currency": "RUB",
    "createdAt": "2026-10-28T12:00:00Z",
}


def test_healthz(client: TestClient) -> None:
    response = client.get("/healthz")

    assert response.status_code == 200
    assert response.text == "ok"


def test_check_answers_in_camel_case(client: TestClient) -> None:
    response = client.post("/v1/checks", json=REQUEST)

    assert response.status_code == 200
    body = response.json()
    assert body["paymentId"] == REQUEST["paymentId"]
    assert body["decision"] in ("allow", "deny")
    assert "modelVersion" in body


def test_repeated_check_gets_the_same_answer(client: TestClient) -> None:
    first = client.post("/v1/checks", json=REQUEST)
    second = client.post("/v1/checks", json=REQUEST)

    assert second.status_code == 200
    assert second.json() == first.json()


@pytest.mark.parametrize(
    ("body", "field"),
    [
        ({**REQUEST, "amount": 0}, "amount"),
        ({**REQUEST, "currency": "RU"}, "currency"),
        ({key: value for key, value in REQUEST.items() if key != "merchantId"}, "merchantId"),
        ({**REQUEST, "createdAt": "2026-10-28T12:00:00"}, "createdAt"),  # no offset: the window is ambiguous
        ({**REQUEST, "merchantClass": "huge"}, "merchantClass"),
        ({**REQUEST, "merchantClass": "large"}, "quotaPerSecond"),  # a large merchant needs its quota
        ({**REQUEST, "merchantClass": "large", "quotaPerSecond": 0}, "quotaPerSecond"),
        ({**REQUEST, "quotaPerSecond": 1000}, "quotaPerSecond"),  # a standard merchant has none
    ],
)
def test_invalid_field_is_rejected_with_400(client: TestClient, body: dict, field: str) -> None:
    response = client.post("/v1/checks", json=body)

    assert response.status_code == 400
    assert response.json()["error"].startswith(f"{field}: ")


def test_malformed_json_is_rejected_with_400(client: TestClient) -> None:
    response = client.post("/v1/checks", content=b'{"paymentId": ', headers={"content-type": "application/json"})

    assert response.status_code == 400
    assert response.json()["error"].startswith("invalid JSON")


def test_large_merchant_with_its_quota_is_checked(client: TestClient) -> None:
    response = client.post("/v1/checks", json={**REQUEST, "merchantClass": "large", "quotaPerSecond": 1000})

    assert response.status_code == 200
    assert response.json()["decision"] == "allow"


@pytest.fixture
def unreachable_redis_client() -> Iterator[TestClient]:
    """The app with a Redis that refuses every command, as if the server were down."""
    server = FakeServer()
    server.connected = False
    fake = FakeAsyncRedis(server=server, decode_responses=True)
    app.dependency_overrides[get_redis] = lambda: fake
    with TestClient(app) as test_client:
        yield test_client
    app.dependency_overrides.clear()


def test_unreachable_redis_answers_503(unreachable_redis_client: TestClient) -> None:
    response = unreachable_redis_client.post("/v1/checks", json=REQUEST)

    assert response.status_code == 503
    assert response.json() == {"error": "redis unavailable"}


@pytest.fixture
def silent_redis_url() -> Iterator[str]:
    """A server that takes connections and never answers: a hung Redis."""
    with socket.create_server(("127.0.0.1", 0)) as server:
        yield f"redis://127.0.0.1:{server.getsockname()[1]}/0"


def test_hung_redis_answers_503_within_the_timeout(silent_redis_url: str, monkeypatch: pytest.MonkeyPatch) -> None:
    # The real Redis client from the app's startup, not a fake: this checks the timeouts it is created with.
    monkeypatch.setenv("ANTIFRAUD_REDIS_URL", silent_redis_url)
    monkeypatch.setenv("ANTIFRAUD_REDIS_TIMEOUT_MS", "20")

    with TestClient(app) as client:
        started = time.perf_counter()
        response = client.post("/v1/checks", json=REQUEST)
        elapsed = time.perf_counter() - started

    assert response.status_code == 503
    assert elapsed < 0.5  # one 20 ms timeout, not the 5 seconds redis-py waits by default
