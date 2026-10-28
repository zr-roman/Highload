from fastapi.testclient import TestClient

from antifraud.main import app

REQUEST = {
    "paymentId": "0192f0c8-0000-7000-8000-000000000001",
    "merchantId": 42,
    "amount": 150000,
    "currency": "RUB",
    "createdAt": "2026-10-28T12:00:00Z",
}


def test_healthz() -> None:
    with TestClient(app) as client:
        response = client.get("/healthz")

    assert response.status_code == 200
    assert response.text == "ok"


def test_check_answers_in_camel_case() -> None:
    with TestClient(app) as client:
        response = client.post("/v1/checks", json=REQUEST)

    assert response.status_code == 200
    body = response.json()
    assert body["paymentId"] == REQUEST["paymentId"]
    assert body["decision"] in ("allow", "deny")
    assert "modelVersion" in body


def test_invalid_request_is_rejected() -> None:
    with TestClient(app) as client:
        response = client.post("/v1/checks", json={**REQUEST, "amount": 0})

    # TODO: the contract says 400; FastAPI answers 422 by default. Decide and make them agree.
    assert response.status_code in (400, 422)
