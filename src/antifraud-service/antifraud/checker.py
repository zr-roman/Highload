from uuid import UUID

from redis.asyncio import Redis

from antifraud.models import CheckRequest, CheckResponse, Decision
from antifraud.settings import Settings
from antifraud.velocity import count_payment, velocity_limit

RULES_VERSION = "rules-2"  # rules-1 allowed every payment; rules-2 adds the velocity rule


def decision_key(payment_id: UUID) -> str:
    return f"af:decision:{payment_id}"


async def check_payment(request: CheckRequest, redis: Redis, settings: Settings) -> CheckResponse:
    """The decision on a payment. Idempotent by payment_id: every check of the same payment gets the same answer."""
    key = decision_key(request.payment_id)

    # A repeat after a timeout gets the stored decision as is: the rules do not run again,
    # so a payment is never counted twice and never decided differently.
    stored = await redis.get(key)
    if stored is not None:
        return CheckResponse.model_validate_json(stored)

    decision = await evaluate_rules(request, redis, settings)

    # NX: only the first decision is stored. If a concurrent request for the same payment stored first,
    # its decision wins, and both callers get the same answer.
    if await redis.set(key, decision.model_dump_json(by_alias=True), nx=True, ex=settings.decision_ttl_seconds):
        return decision

    stored = await redis.get(key)
    # None only if the winner's key is already gone (expired or deleted); then there is nothing to agree with.
    return CheckResponse.model_validate_json(stored) if stored is not None else decision


async def evaluate_rules(request: CheckRequest, redis: Redis, settings: Settings) -> CheckResponse:
    number_in_window = await count_payment(request, redis, settings)

    # The rules are hard limits, so the score is 0 or 1; a model will give values in between.
    if number_in_window > velocity_limit(request, settings):
        return CheckResponse(
            payment_id=request.payment_id,
            decision=Decision.DENY,
            score=1.0,
            reasons=["velocity"],
            model_version=RULES_VERSION,
        )

    return CheckResponse(
        payment_id=request.payment_id,
        decision=Decision.ALLOW,
        score=0.0,
        reasons=[],
        model_version=RULES_VERSION,
    )
