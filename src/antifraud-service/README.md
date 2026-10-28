# Антифрод (Python)

Проверка платежа после авторизации в банке: пропустить или отклонить. Payments.Api вызывает его на синхронном пути платежа, поэтому главное здесь — бюджет задержки и поведение при сбоях. Отказ антифрода отменяет авторизацию в банке — это сага из задачи 9 этапа 2.

Python 3.12, FastAPI + uvicorn, Pydantic, `redis.asyncio`; тесты — pytest, линтер — ruff, зависимости — uv.

## API

**`POST /v1/checks`**

```json
{"paymentId": "0192f0c8-0000-7000-8000-000000000001", "merchantId": 42, "amount": 150000, "currency": "RUB", "createdAt": "2026-10-28T12:00:00Z"}
```

```json
{"paymentId": "0192f0c8-0000-7000-8000-000000000001", "decision": "allow", "score": 0.0, "reasons": [], "modelVersion": "rules-1"}
```

`decision` — `allow` или `deny`; `score` — риск от 0 до 1; `reasons` — коды сработавших правил; `modelVersion` — какие правила или модель приняли решение. Сумма — в копейках.

**`GET /healthz`** — 200 и `ok`.

## Настройки

| Переменная | По умолчанию | Что делает |
|---|---|---|
| `ANTIFRAUD_REDIS_URL` | `redis://127.0.0.1:6379/0` | Redis для счётчиков и сохранённых решений |
| `ANTIFRAUD_VELOCITY_LIMIT` | 100 | не больше стольких платежей одного продавца… |
| `ANTIFRAUD_VELOCITY_WINDOW_SECONDS` | 60 | …за такое окно |
| `ANTIFRAUD_DECISION_TTL_SECONDS` | 86400 | сколько хранится решение для повторов |

## Запуск

Один раз — uv (он же поставит Python 3.12):

```powershell
winget install --id=astral-sh.uv -e
```

Redis поднимается вместе с Kafka: `docker compose up -d` в корне репозитория.

```powershell
cd src\antifraud-service
uv sync
uv run uvicorn antifraud.main:app --host 127.0.0.1 --port 5400 --workers 4
```

`--workers` — несколько процессов: из-за GIL один процесс Python занимает одно ядро.

Проверка:

```powershell
$body = '{"paymentId":"0192f0c8-0000-7000-8000-000000000001","merchantId":42,"amount":150000,"currency":"RUB","createdAt":"2026-10-28T12:00:00Z"}'
Invoke-RestMethod -Method Post -Uri http://localhost:5400/v1/checks -ContentType "application/json" -Body $body
```

## Тесты и линтер

```powershell
uv run pytest
uv run ruff check .
uv run ruff format .
```

## Структура

- `antifraud/main.py` — приложение FastAPI, маршруты, Redis и настройки на старте процесса.
- `antifraud/models.py` — контракт: запрос и ответ, JSON в camelCase.
- `antifraud/checker.py` — решение по платежу. Сейчас заглушка: всегда `allow`.
- `antifraud/features.py` — признаки платежа, общие для сервиса и обучения модели.
- `antifraud/settings.py` — настройки из переменных окружения.
- `tests/test_api.py` — тесты контракта.

## Что дальше (TODO в коде)

1. Идемпотентность по `paymentId`: повтор получает сохранённое решение и не увеличивает счётчик частоты.
2. Правило частоты на Redis.
3. Код ответа на неверный запрос: в контракте 400, FastAPI по умолчанию отвечает 422.
4. Тесты на правила и идемпотентность.
5. Телеметрия OpenTelemetry в тот же дашборд.
6. Замер k6: p99 не больше 20 мс при 1000 RPS.
7. Модель вместо правил — после того, как правила работают.
