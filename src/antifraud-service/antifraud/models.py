"""The HTTP contract. JSON is camelCase, like the rest of the platform; Python code uses snake_case."""

from enum import StrEnum
from uuid import UUID

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, ValidationInfo, field_validator
from pydantic.alias_generators import to_camel
from pydantic_core import PydanticCustomError


class ApiModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, frozen=True)


class MerchantClass(StrEnum):
    STANDARD = "standard"
    LARGE = "large"  # a merchant with its own rate limit quota in Payments.Api


class CheckRequest(ApiModel):
    payment_id: UUID
    merchant_id: int = Field(gt=0)
    amount: int = Field(gt=0)  # minor units (kopecks), as in Payments.Api
    currency: str = Field(min_length=3, max_length=3)
    # With an offset: the velocity window is taken from this time, and a time without an offset is ambiguous.
    created_at: AwareDatetime

    # Payments.Api knows which merchants have their own quota and sends the class and the quota with every check,
    # so the quotas live in one place and are not duplicated in the antifraud settings.
    merchant_class: MerchantClass = MerchantClass.STANDARD
    # validate_default: the check below runs when the field is left out too, so a large merchant without a quota
    # is rejected instead of being judged by the standard limit.
    quota_per_second: float | None = Field(default=None, gt=0, validate_default=True)

    @field_validator("quota_per_second")
    @classmethod
    def quota_goes_with_large_class(cls, quota: float | None, info: ValidationInfo) -> float | None:
        merchant_class = info.data.get("merchant_class")  # absent if the class itself was invalid

        if merchant_class is MerchantClass.LARGE and quota is None:
            raise PydanticCustomError("quota_required", "required for a large merchant")
        if merchant_class is MerchantClass.STANDARD and quota is not None:
            raise PydanticCustomError("quota_not_allowed", "only a large merchant has a quota")

        return quota


class Decision(StrEnum):
    ALLOW = "allow"
    DENY = "deny"


class CheckResponse(ApiModel):
    payment_id: UUID
    decision: Decision
    score: float = Field(ge=0, le=1)
    reasons: list[str] = []  # machine codes of the rules that fired: "velocity", "amount_outlier"
    model_version: str  # which rules or model decided
