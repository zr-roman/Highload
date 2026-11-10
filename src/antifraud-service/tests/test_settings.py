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
