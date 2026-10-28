"""The HTTP contract. JSON is camelCase, like the rest of the platform; Python code uses snake_case."""

from datetime import datetime
from enum import StrEnum
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class ApiModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, frozen=True)


class CheckRequest(ApiModel):
    payment_id: UUID
    merchant_id: int = Field(gt=0)
    amount: int = Field(gt=0)  # minor units (kopecks), as in Payments.Api
    currency: str = Field(min_length=3, max_length=3)
    created_at: datetime


class Decision(StrEnum):
    ALLOW = "allow"
    DENY = "deny"


class CheckResponse(ApiModel):
    payment_id: UUID
    decision: Decision
    score: float = Field(ge=0, le=1)
    reasons: list[str] = []  # machine codes of the rules that fired: "velocity", "amount_outlier"
    model_version: str  # which rules or model decided
