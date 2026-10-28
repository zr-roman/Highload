from uuid import UUID

from redis.asyncio import Redis

from antifraud.models import CheckRequest, CheckResponse, Decision
from antifraud.settings import Settings

RULES_VERSION = "rules-1"


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
    # TODO 2. Velocity: count the merchant's payments in the window (INCR + EXPIRE on a per-merchant key).

    # TODO 3. Rules -> decision, score and reasons.

    return CheckResponse(
        payment_id=request.payment_id,
        decision=Decision.ALLOW,
        score=0.0,
        reasons=[],
        model_version=RULES_VERSION,
    )
