"""The velocity rule: at most N payments of one merchant within a fixed window, counted in Redis."""

import math
from uuid import UUID

from redis.asyncio import Redis

from antifraud.models import CheckRequest, MerchantClass
from antifraud.settings import Settings

# Counts a payment once and returns its number in the window. The mark keeps that number, so a duplicate that
# missed the stored decision (it came while the first check was still running) gets the same number back and
# adds nothing. A script runs in Redis as one step: two duplicates cannot both find no mark and both increment.
# KEYS[1] is the payment's mark, KEYS[2] the merchant's counter for the window; ARGV[1] is the TTL of both, seconds.
# The two keys are in different hash slots: fine on one Redis, a Redis Cluster would need a hash tag for both.
COUNT_PAYMENT = """
local counted = redis.call('GET', KEYS[1])
if counted then
    return tonumber(counted)
end
local count = redis.call('INCR', KEYS[2])
if count == 1 then
    redis.call('EXPIRE', KEYS[2], ARGV[1])
end
redis.call('SET', KEYS[1], count, 'EX', ARGV[1])
return count
"""


def counter_key(merchant_id: int, window: int) -> str:
    return f"af:velocity:{merchant_id}:{window}"


def mark_key(payment_id: UUID) -> str:
    return f"af:counted:{payment_id}"


def window_of(request: CheckRequest, window_seconds: int) -> int:
    """The number of the fixed window the payment falls into.

    By the payment's created_at, not by the time of the check: payments that waited in the background for a recheck
    (antifraud was down) are counted in the windows they were made in and do not pile up in the current one.
    """
    return int(request.created_at.timestamp()) // window_seconds


def velocity_limit(request: CheckRequest, settings: Settings) -> int:
    """How many payments of the merchant one window lets through."""
    if request.merchant_class is MerchantClass.LARGE:
        # A large merchant is held to its own quota: the rule must not deny the pace that the rate limiter
        # of Payments.Api lets through. The request model guarantees a quota for a large merchant.
        return math.floor(request.quota_per_second * settings.velocity_window_seconds)

    return settings.velocity_limit


async def count_payment(request: CheckRequest, redis: Redis, settings: Settings) -> int:
    """Counts the payment in its merchant's window, once, and returns its number there: 1 for the first one."""
    window = window_of(request, settings.velocity_window_seconds)

    # The counter outlives its window by one more window, so a payment checked a little late still finds it.
    # The mark lives as long: once the decision is stored, a repeat gets it and never runs the script,
    # so the mark only has to cover the duplicates that come before that.
    ttl_seconds = 2 * settings.velocity_window_seconds

    script = redis.register_script(COUNT_PAYMENT)
    return await script(
        keys=[mark_key(request.payment_id), counter_key(request.merchant_id, window)], args=[ttl_seconds]
    )
