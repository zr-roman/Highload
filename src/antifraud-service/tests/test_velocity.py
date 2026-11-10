import asyncio
import dataclasses
from datetime import UTC, datetime, timedelta
from uuid import UUID, uuid4

import pytest
from fakeredis import FakeAsyncRedis

from antifraud.checker import RULES_VERSION, check_payment, evaluate_rules
from antifraud.models import CheckRequest, Decision, MerchantClass
from antifraud.settings import Settings
from antifraud.velocity import count_payment, counter_key, mark_key, velocity_limit, window_of

# 12:00:00 sharp starts a 60-second window, so a minute later is the next one.
NOON = datetime(2026, 10, 28, 12, 0, tzinfo=UTC)
MERCHANT = 42

# 2 payments a second over the 60-second window: 120, more than the standard limit of 100.
LARGE = {"merchant_class": MerchantClass.LARGE, "quota_per_second": 2.0}


def payment(
    *,
    payment_id: UUID | None = None,
    merchant_id: int = MERCHANT,
    created_at: datetime = NOON,
    merchant_class: MerchantClass = MerchantClass.STANDARD,
    quota_per_second: float | None = None,
) -> CheckRequest:
    return CheckRequest(
        payment_id=payment_id or uuid4(),
        merchant_id=merchant_id,
        amount=150000,
        currency="RUB",
        created_at=created_at,
        merchant_class=merchant_class,
        quota_per_second=quota_per_second,
    )


async def check_many(count: int, redis: FakeAsyncRedis, settings: Settings, **fields: object) -> list[Decision]:
    """Checks count new payments of one merchant in turn and returns the decisions."""
    return [(await check_payment(payment(**fields), redis, settings)).decision for _ in range(count)]


async def counted(redis: FakeAsyncRedis, settings: Settings, created_at: datetime = NOON) -> int:
    """The merchant's counter for the window of created_at."""
    window = window_of(payment(created_at=created_at), settings.velocity_window_seconds)
    value = await redis.get(counter_key(MERCHANT, window))
    return int(value) if value is not None else 0


async def test_standard_merchant_is_denied_after_the_limit(redis: FakeAsyncRedis, settings: Settings) -> None:
    allowed = await check_many(settings.velocity_limit, redis, settings)
    over = await check_payment(payment(), redis, settings)

    assert allowed == [Decision.ALLOW] * settings.velocity_limit
    assert over.decision is Decision.DENY
    assert over.reasons == ["velocity"]
    assert over.score == 1.0
    assert over.model_version == RULES_VERSION


async def test_large_merchant_passes_its_own_pace(redis: FakeAsyncRedis, settings: Settings) -> None:
    allowed = await check_many(120, redis, settings, **LARGE)
    over = await check_payment(payment(**LARGE), redis, settings)

    assert allowed == [Decision.ALLOW] * 120
    assert over.decision is Decision.DENY


@pytest.mark.parametrize(("quota", "limit"), [(2.0, 120), (2.5, 150), (0.5, 30), (1000.0, 60000)])
def test_limit_of_a_large_merchant_is_its_quota_over_the_window(settings: Settings, quota: float, limit: int) -> None:
    request = payment(merchant_class=MerchantClass.LARGE, quota_per_second=quota)

    assert velocity_limit(request, settings) == limit


async def test_repeat_is_counted_once(redis: FakeAsyncRedis, settings: Settings) -> None:
    request = payment()

    first = await check_payment(request, redis, settings)
    second = await check_payment(request, redis, settings)

    assert second == first
    assert await counted(redis, settings) == 1


async def test_concurrent_duplicates_are_counted_once(redis: FakeAsyncRedis, settings: Settings) -> None:
    request = payment()

    first, second = await asyncio.gather(
        check_payment(request, redis, settings),
        check_payment(request, redis, settings),
    )

    assert first == second
    assert await counted(redis, settings) == 1


async def test_duplicate_that_missed_the_stored_decision_is_counted_once(
    redis: FakeAsyncRedis, settings: Settings
) -> None:
    # Two duplicates that both came before a decision was stored both run the rules; the mark keeps the count.
    request = payment()

    first = await count_payment(request, redis, settings)
    second = await count_payment(request, redis, settings)

    assert first == second == 1
    assert await counted(redis, settings) == 1


async def test_duplicate_keeps_its_place_in_the_window(redis: FakeAsyncRedis, settings: Settings) -> None:
    await check_many(settings.velocity_limit - 1, redis, settings)
    last_allowed = payment()
    await check_payment(last_allowed, redis, settings)
    await check_payment(payment(), redis, settings)  # the first one over the limit

    # A duplicate of the last allowed payment that runs the rules again gets its own number back,
    # not the next one, so it is not denied for the payments that came after it.
    again = await evaluate_rules(last_allowed, redis, settings)

    assert again.decision is Decision.ALLOW


async def test_next_window_starts_from_zero(redis: FakeAsyncRedis, settings: Settings) -> None:
    await check_many(settings.velocity_limit + 1, redis, settings)
    next_window = NOON + timedelta(seconds=settings.velocity_window_seconds)

    result = await check_payment(payment(created_at=next_window), redis, settings)

    assert result.decision is Decision.ALLOW
    assert await counted(redis, settings, created_at=next_window) == 1


async def test_merchants_are_counted_separately(redis: FakeAsyncRedis, settings: Settings) -> None:
    await check_many(settings.velocity_limit + 1, redis, settings)

    other = await check_payment(payment(merchant_id=MERCHANT + 1), redis, settings)

    assert other.decision is Decision.ALLOW


async def test_zero_limit_denies_every_payment(redis: FakeAsyncRedis, settings: Settings) -> None:
    no_payments = dataclasses.replace(settings, velocity_limit=0)

    result = await check_payment(payment(), redis, no_payments)

    assert result.decision is Decision.DENY


async def test_counter_and_mark_expire(redis: FakeAsyncRedis, settings: Settings) -> None:
    request = payment()

    await check_payment(request, redis, settings)

    window = window_of(request, settings.velocity_window_seconds)
    for key in (counter_key(MERCHANT, window), mark_key(request.payment_id)):
        ttl = await redis.ttl(key)
        assert 0 < ttl <= 2 * settings.velocity_window_seconds, key
