import asyncio
from datetime import UTC, datetime
from uuid import UUID

import pytest
from fakeredis import FakeAsyncRedis

from antifraud import checker
from antifraud.checker import check_payment, decision_key
from antifraud.models import CheckRequest, CheckResponse, Decision
from antifraud.settings import Settings


def make_request(payment_id: str = "0192f0c8-0000-7000-8000-000000000001") -> CheckRequest:
    return CheckRequest(
        payment_id=UUID(payment_id),
        merchant_id=42,
        amount=150000,
        currency="RUB",
        created_at=datetime(2026, 10, 28, 12, 0, tzinfo=UTC),
    )


class ScriptedRules:
    """Stands in for the rules: answers with the given decisions in turn and counts the calls."""

    def __init__(self, *decisions: Decision) -> None:
        self._decisions = iter(decisions)
        self.calls = 0

    async def __call__(self, request: CheckRequest, redis: FakeAsyncRedis, settings: Settings) -> CheckResponse:
        self.calls += 1
        await asyncio.sleep(0)  # a concurrent duplicate gets to run up to this point too
        decision = next(self._decisions)
        return CheckResponse(
            payment_id=request.payment_id,
            decision=decision,
            score=1.0 if decision is Decision.DENY else 0.0,
            reasons=["test"] if decision is Decision.DENY else [],
            model_version="test",
        )


async def test_repeat_gets_the_stored_decision(
    redis: FakeAsyncRedis, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    rules = ScriptedRules(Decision.DENY, Decision.ALLOW)
    monkeypatch.setattr(checker, "evaluate_rules", rules)

    first = await check_payment(make_request(), redis, settings)
    second = await check_payment(make_request(), redis, settings)

    assert first.decision is Decision.DENY
    assert second == first  # the rules would now allow, but the stored decision wins
    assert rules.calls == 1  # the repeat did not run the rules, so it counted nothing


async def test_concurrent_duplicates_get_one_decision(
    redis: FakeAsyncRedis, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    rules = ScriptedRules(Decision.DENY, Decision.ALLOW)
    monkeypatch.setattr(checker, "evaluate_rules", rules)

    first, second = await asyncio.gather(
        check_payment(make_request(), redis, settings),
        check_payment(make_request(), redis, settings),
    )

    assert rules.calls == 2  # both requests missed the stored decision and ran the rules...
    assert first == second  # ...but only one decision was stored, and both callers got it


async def test_decision_is_kept_for_the_ttl(redis: FakeAsyncRedis, settings: Settings) -> None:
    request = make_request()

    await check_payment(request, redis, settings)

    ttl = await redis.ttl(decision_key(request.payment_id))
    assert 0 < ttl <= settings.decision_ttl_seconds


async def test_payments_are_decided_independently(
    redis: FakeAsyncRedis, settings: Settings, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(checker, "evaluate_rules", ScriptedRules(Decision.DENY, Decision.ALLOW))

    first = await check_payment(make_request("0192f0c8-0000-7000-8000-000000000001"), redis, settings)
    second = await check_payment(make_request("0192f0c8-0000-7000-8000-000000000002"), redis, settings)

    assert first.decision is Decision.DENY
    assert second.decision is Decision.ALLOW
