from collections.abc import AsyncIterator, Iterator

import pytest
from fakeredis import FakeAsyncRedis
from fastapi.testclient import TestClient

from antifraud.main import app, get_redis
from antifraud.settings import Settings


@pytest.fixture
def settings() -> Settings:
    return Settings(
        redis_url="redis://unused",
        velocity_limit=100,
        velocity_window_seconds=60,
        decision_ttl_seconds=86400,
    )


@pytest.fixture
async def redis() -> AsyncIterator[FakeAsyncRedis]:
    """Redis in memory with the same API: tests of the logic need no running server."""
    client = FakeAsyncRedis(decode_responses=True)
    yield client
    await client.aclose()


@pytest.fixture
def client() -> Iterator[TestClient]:
    """The whole app over HTTP, with the in-memory Redis in place of the real one."""
    fake = FakeAsyncRedis(decode_responses=True)
    app.dependency_overrides[get_redis] = lambda: fake
    with TestClient(app) as test_client:
        yield test_client
    app.dependency_overrides.clear()
