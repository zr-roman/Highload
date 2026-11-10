import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    """Read once at startup from environment variables; a wrong value fails the start."""

    redis_url: str
    velocity_limit: int  # at most this many payments of one standard merchant...
    velocity_window_seconds: int  # ...within this window
    decision_ttl_seconds: int  # how long a decision is kept for idempotent repeats
    redis_timeout_ms: int  # a Redis call slower than this counts as Redis being down

    def __post_init__(self) -> None:
        # 0 is allowed on purpose: every payment is denied, which is how the saga's compensation is tested.
        if self.velocity_limit < 0:
            raise ValueError(f"ANTIFRAUD_VELOCITY_LIMIT must be 0 or more, got {self.velocity_limit}")
        if self.velocity_window_seconds < 1:
            raise ValueError(f"ANTIFRAUD_VELOCITY_WINDOW_SECONDS must be 1 or more, got {self.velocity_window_seconds}")
        if self.decision_ttl_seconds < 1:
            raise ValueError(f"ANTIFRAUD_DECISION_TTL_SECONDS must be 1 or more, got {self.decision_ttl_seconds}")
        if self.redis_timeout_ms < 1:
            raise ValueError(f"ANTIFRAUD_REDIS_TIMEOUT_MS must be 1 or more, got {self.redis_timeout_ms}")

    @classmethod
    def from_env(cls) -> "Settings":
        return cls(
            redis_url=os.environ.get("ANTIFRAUD_REDIS_URL", "redis://127.0.0.1:6379/0"),
            velocity_limit=int(os.environ.get("ANTIFRAUD_VELOCITY_LIMIT", "100")),
            velocity_window_seconds=int(os.environ.get("ANTIFRAUD_VELOCITY_WINDOW_SECONDS", "60")),
            decision_ttl_seconds=int(os.environ.get("ANTIFRAUD_DECISION_TTL_SECONDS", "86400")),
            redis_timeout_ms=int(os.environ.get("ANTIFRAUD_REDIS_TIMEOUT_MS", "20")),
        )
