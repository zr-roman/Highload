import pytest
from fastapi.testclient import TestClient

REQUEST = {
    "paymentId": "0192f0c8-0000-7000-8000-000000000001",
    "merchantId": 42,
    "amount": 150000,
    "currency": "RUB",
    "createdAt": "2026-10-28T12:00:00Z",
}


def test_healthz(client: TestClient) -> None:
    response = client.get("/healthz")

    assert response.status_code == 200
    assert response.text == "ok"


def test_check_answers_in_camel_case(client: TestClient) -> None:
    response = client.post("/v1/checks", json=REQUEST)

    assert response.status_code == 200
    body = response.json()
    assert body["paymentId"] == REQUEST["paymentId"]
    assert body["decision"] in ("allow", "deny")
    assert "modelVersion" in body


def test_repeated_check_gets_the_same_answer(client: TestClient) -> None:
    first = client.post("/v1/checks", json=REQUEST)
    second = client.post("/v1/checks", json=REQUEST)

    assert second.status_code == 200
    assert second.json() == first.json()


@pytest.mark.parametrize(
    ("body", "field"),
    [
        ({**REQUEST, "amount": 0}, "amount"),
        ({**REQUEST, "currency": "RU"}, "currency"),
        ({key: value for key, value in REQUEST.items() if key != "merchantId"}, "merchantId"),
    ],
)
def test_invalid_field_is_rejected_with_400(client: TestClient, body: dict, field: str) -> None:
    response = client.post("/v1/checks", json=body)

    assert response.status_code == 400
    assert response.json()["error"].startswith(f"{field}: ")


def test_malformed_json_is_rejected_with_400(client: TestClient) -> None:
    response = client.post("/v1/checks", content=b'{"paymentId": ', headers={"content-type": "application/json"})

    assert response.status_code == 400
    assert response.json()["error"].startswith("invalid JSON")
