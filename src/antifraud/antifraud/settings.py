import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    """Read once at startup from environment variables; a wrong value fails the start."""

    redis_url: str
    velocity_limit: int  # at most this many payments of one merchant...
    velocity_window_seconds: int  # ...within this window
    decision_ttl_seconds: int  # how long a decision is kept for idempotent repeats

    @classmethod
    def from_env(cls) -> "Settings":
        return cls(
            redis_url=os.environ.get("ANTIFRAUD_REDIS_URL", "redis://127.0.0.1:6379/0"),
            velocity_limit=int(os.environ.get("ANTIFRAUD_VELOCITY_LIMIT", "100")),
            velocity_window_seconds=int(os.environ.get("ANTIFRAUD_VELOCITY_WINDOW_SECONDS", "60")),
            decision_ttl_seconds=int(os.environ.get("ANTIFRAUD_DECISION_TTL_SECONDS", "86400")),
        )
