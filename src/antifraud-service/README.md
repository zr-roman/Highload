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

Проверка идемпотентна по `paymentId`: решение хранится в Redis, и повтор получает его как есть, даже если правила теперь ответили бы иначе. Поэтому Payments.Api может безопасно повторять запрос после таймаута. Неверный запрос — 400 с телом `{"error": "..."}`, как у эмулятора банка.

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

Тестам не нужен запущенный Redis: вместо него — fakeredis, Redis в памяти с тем же API.

## Структура

- `antifraud/main.py` — приложение FastAPI, маршруты, Redis и настройки на старте процесса.
- `antifraud/models.py` — контракт: запрос и ответ, JSON в camelCase.
- `antifraud/checker.py` — решение по платежу: идемпотентность по `paymentId` готова, правила пока заглушка — всегда `allow`.
- `antifraud/features.py` — признаки платежа, общие для сервиса и обучения модели.
- `antifraud/settings.py` — настройки из переменных окружения.
- `tests/conftest.py` — общие фикстуры: настройки, Redis в памяти, приложение с подменённым Redis.
- `tests/test_api.py` — контракт через HTTP: ответ в camelCase, повтор, 400 на неверный запрос.
- `tests/test_checker.py` — идемпотентность: повтор, одновременные дубли, срок хранения решения.

## Что дальше (TODO в коде)

1. Правило частоты на Redis и тесты на него.
2. Ответ 503, когда Redis недоступен.
3. Телеметрия OpenTelemetry в тот же дашборд.
4. Замер k6: p99 не больше 20 мс при 1000 RPS.
5. Модель вместо правил — после того, как правила работают.
