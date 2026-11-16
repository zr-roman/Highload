import pytest

from antifraud.settings import Settings


def test_defaults(monkeypatch: pytest.MonkeyPatch) -> None:
    for name in (
        "ANTIFRAUD_REDIS_URL",
        "ANTIFRAUD_VELOCITY_LIMIT",
        "ANTIFRAUD_VELOCITY_WINDOW_SECONDS",
        "ANTIFRAUD_DECISION_TTL_SECONDS",
        "ANTIFRAUD_REDIS_TIMEOUT_MS",
    ):
        monkeypatch.delenv(name, raising=False)

    settings = Settings.from_env()

    assert settings.velocity_limit == 100
    assert settings.velocity_window_seconds == 60
    assert settings.redis_timeout_ms == 20


def test_zero_limit_is_allowed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ANTIFRAUD_VELOCITY_LIMIT", "0")

    assert Settings.from_env().velocity_limit == 0


@pytest.mark.parametrize(
    ("name", "value"),
    [
        ("ANTIFRAUD_VELOCITY_LIMIT", "-1"),
        ("ANTIFRAUD_VELOCITY_WINDOW_SECONDS", "0"),
        ("ANTIFRAUD_DECISION_TTL_SECONDS", "0"),
        ("ANTIFRAUD_REDIS_TIMEOUT_MS", "0"),
    ],
)
def test_wrong_value_fails_the_start(monkeypatch: pytest.MonkeyPatch, name: str, value: str) -> None:
    monkeypatch.setenv(name, value)

    with pytest.raises(ValueError, match=name):
        Settings.from_env()


def test_sentinels(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("ANTIFRAUD_REDIS_SENTINELS", "redis-0.redis:26379, redis-1.redis:26379")

    settings = Settings.from_env()

    assert settings.redis_sentinels == (("redis-0.redis", 26379), ("redis-1.redis", 26379))
    assert settings.redis_master == "mymaster"


def test_no_sentinels_by_default(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("ANTIFRAUD_REDIS_SENTINELS", raising=False)

    assert Settings.from_env().redis_sentinels == ()


@pytest.mark.parametrize("value", ["redis-0.redis", "redis-0.redis:port", ":26379"])
def test_bad_sentinels(monkeypatch: pytest.MonkeyPatch, value: str) -> None:
    monkeypatch.setenv("ANTIFRAUD_REDIS_SENTINELS", value)

    with pytest.raises(ValueError, match="ANTIFRAUD_REDIS_SENTINELS"):
        Settings.from_env()
