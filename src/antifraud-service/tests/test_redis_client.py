"""The Redis client of the app fails at once when the Sentinels do not answer: no retries.

Against servers that take the connection and never answer, the way a Sentinel short of processor looks to the
client. Before the fix redis-py retried the connection to each Sentinel 10 times with pauses up to a second: a check
whose three Sentinels were slow answered 503 after 29 s (docs/load-testing.md, "Этап 4: учения саги на стенде").
"""

import asyncio
import time
from collections.abc import AsyncIterator
from dataclasses import replace

import pytest
from redis.exceptions import RedisError

from antifraud.main import connect_redis
from antifraud.settings import Settings


async def silent(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    """Reads whatever comes and never answers."""
    while await reader.read(1024):
        pass


@pytest.fixture
async def silent_ports() -> AsyncIterator[list[int]]:
    servers = [await asyncio.start_server(silent, "127.0.0.1", 0) for _ in range(3)]
    yield [server.sockets[0].getsockname()[1] for server in servers]
    for server in servers:
        server.close()


async def test_silent_sentinels_fail_in_one_timeout_each(settings: Settings, silent_ports: list[int]) -> None:
    redis = connect_redis(replace(settings, redis_sentinels=tuple(("127.0.0.1", port) for port in silent_ports)))
    started = time.monotonic()

    with pytest.raises(RedisError):
        await redis.get("af:decision:any")
    seconds = time.monotonic() - started
    await redis.aclose()

    # Three Sentinels, 0.5 s each, once: 1.5 s. With the retries of redis-py it was 29 s.
    assert seconds < 3
