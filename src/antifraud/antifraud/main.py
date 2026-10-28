from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI, Request
from fastapi.responses import PlainTextResponse
from redis.asyncio import Redis

from antifraud.checker import check_payment
from antifraud.models import CheckRequest, CheckResponse
from antifraud.settings import Settings


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    # One settings object and one Redis connection pool per worker process, created at startup.
    settings = Settings.from_env()
    app.state.settings = settings
    app.state.redis = Redis.from_url(settings.redis_url, decode_responses=True)
    yield
    await app.state.redis.aclose()


app = FastAPI(title="antifraud", lifespan=lifespan)


def get_settings(request: Request) -> Settings:
    return request.app.state.settings


def get_redis(request: Request) -> Redis:
    return request.app.state.redis


@app.get("/healthz", response_class=PlainTextResponse)
async def healthz() -> str:
    return "ok"


@app.post("/v1/checks")
async def checks(
    request: CheckRequest,
    settings: Annotated[Settings, Depends(get_settings)],
    redis: Annotated[Redis, Depends(get_redis)],
) -> CheckResponse:
    return await check_payment(request, redis, settings)
