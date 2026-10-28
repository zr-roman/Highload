from redis.asyncio import Redis

from antifraud.models import CheckRequest, CheckResponse, Decision
from antifraud.settings import Settings

RULES_VERSION = "rules-1"


async def check_payment(request: CheckRequest, redis: Redis, settings: Settings) -> CheckResponse:
    # TODO 1. Idempotency by payment_id: if a decision for this payment is already stored, return it as is.
    #         A retry after a timeout must not get a different answer, and must not count twice in step 2.

    # TODO 2. Velocity: count the merchant's payments in the window (INCR + EXPIRE on a per-merchant key).

    # TODO 3. Rules -> decision, score and reasons.

    # TODO 4. Store the decision (SET NX with decision_ttl_seconds). If NX lost the race,
    #         another request for the same payment_id decided first: return its decision instead.

    return CheckResponse(
        payment_id=request.payment_id,
        decision=Decision.ALLOW,
        score=0.0,
        reasons=[],
        model_version=RULES_VERSION,
    )
