from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Annotated

from fastapi import Depends, FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse, PlainTextResponse
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


def error_response(status_code: int, message: str) -> JSONResponse:
    """Errors have the same shape as in the bank emulator: {"error": "..."}."""
    return JSONResponse(status_code=status_code, content={"error": message})


def describe_validation_errors(exc: RequestValidationError) -> str:
    """For example: "amount: Input should be greater than 0; currency: String should have at least 3 characters"."""
    problems = []
    for error in exc.errors():
        loc = tuple(error["loc"])
        if error["type"] == "json_invalid":
            problems.append(f"invalid JSON at position {loc[-1]}: {error.get('ctx', {}).get('error', '')}")
            continue

        # loc is ("body", "amount"): the client knows the field by its JSON name, so "body" is dropped.
        field = ".".join(str(part) for part in loc[1:]) if len(loc) > 1 else "body"
        problems.append(f"{field}: {error['msg']}")

    return "; ".join(problems)


@app.exception_handler(RequestValidationError)
async def invalid_request(request: Request, exc: RequestValidationError) -> JSONResponse:
    # FastAPI answers 422 by default; the contract says 400, like the bank emulator.
    # Payments.Api treats 400 as a bug on its own side and 5xx as "outcome unknown", so the two must not mix.
    return error_response(400, describe_validation_errors(exc))


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
