# Стенд в Kubernetes (k3d), этап 1

Четыре сервиса, Kafka, Redis и дашборд телеметрии — в кластере k3d; PostgreSQL — нативно на хосте (порт 5433), поды ходят к нему по имени `host.docker.internal`.

| Манифест | Что | Порт в кластере |
|---|---|---|
| `kafka.yaml` | Kafka 4.3.2, один узел KRaft, StatefulSet с томом | `kafka:9092` |
| `redis.yaml` | Redis без персистентности | `redis:6379` |
| `dashboard.yaml` | Aspire dashboard: UI и приём OTLP | `dashboard:18888`, `dashboard:18889` |
| `bank-emulator.yaml` | эмулятор банка (Go), одна реплика: решения в памяти | `bank-emulator:5300` |
| `antifraud.yaml` | антифрод (Python) | `antifraud:8000` |
| `payments-api.yaml` | Payments.Api | `payments-api:8080` |
| `ledger-service.yaml` | Ledger.Service | `ledger-service:8080` |
| `ingress.yaml` | вход через Traefik | `localhost:8080` снаружи |

## Запуск

1. **Кластер** (один раз):
   ```powershell
   k3d cluster create highload --agents 2 -p "8080:80@loadbalancer"
   ```
2. **Образы сервисов** — собрать и загрузить в ноды. Тег `:dev`, `imagePullPolicy: IfNotPresent`: кластер берёт образ из нод и не ходит за ним в реестр.
   ```powershell
   docker build -t payments-api:dev   -f src/Payments.Api/Dockerfile .
   docker build -t ledger-service:dev -f src/Ledger.Service/Dockerfile .
   docker build -t bank-emulator:dev  -f src/bank-emulator/Dockerfile .
   docker build -t antifraud:dev      -f src/antifraud-service/Dockerfile .
   k3d image import payments-api:dev ledger-service:dev bank-emulator:dev antifraud:dev -c highload
   ```
   Образы Kafka, Redis и дашборда ноды скачают сами. Если они уже есть локально после `docker compose`, их можно загрузить так же, без повторного скачивания:
   ```powershell
   k3d image import apache/kafka:4.3.2 redis:7.4-alpine mcr.microsoft.com/dotnet/aspire-dashboard:9.0 -c highload
   ```
3. **Пароль Postgres** (один раз). Строки подключения с паролем — в `postgres.env`, его нет в git:
   ```powershell
   Copy-Item deploy\k8s\postgres.env.example deploy\k8s\postgres.env
   notepad deploy\k8s\postgres.env
   ```
   Заменить `CHANGE_ME` в обеих строках. Из файла kustomize создаёт Secret `postgres-<хеш>`; сменился пароль — сменился хеш, и поды перезапускаются сами.
4. **Остановить стенд docker compose** — на два стенда сразу памяти не хватит:
   ```powershell
   docker compose down
   ```
5. **Развернуть:**
   ```powershell
   kubectl apply -k deploy/k8s
   kubectl -n highload get pods -w
   ```
   Пока Kafka не готова, Payments.Api и Ledger.Service падают на создании топика и перезапускаются (`CrashLoopBackOff`) — это нормально, через минуту-две все поды `Running` и `1/1`.

## Проверка

Всё доступно через Traefik на `http://localhost:8080` (`ingress.yaml`): Traefik распределяет запросы по всем готовым подам и не рвётся при их перезапуске, в отличие от `kubectl port-forward`.

| Адрес | Куда |
|---|---|
| `http://localhost:8080/payments` | Payments.Api |
| `http://localhost:8080/balances/...`, `/invariants` | Ledger.Service |
| `http://dashboard.localhost:8080` | дашборд телеметрии (в браузере) |

```powershell
$body = '{"merchantId": 42, "amount": 150000, "currency": "RUB"}'
$headers = @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }
Invoke-RestMethod -Method Post -Uri http://localhost:8080/payments -Headers $headers -ContentType "application/json" -Body $body
Invoke-RestMethod http://localhost:8080/balances/merchants/42
Invoke-RestMethod http://localhost:8080/invariants
```

Нагрузка — тот же сценарий k6, адрес через `BASE_URL`:
```powershell
k6 run -e BASE_URL=http://localhost:8080 -e RATE=200 -e DURATION=60s load/payments.js
```

С какого адреса поды приходят в Postgres:
```powershell
psql -p 5433 -U postgres -c "SELECT DISTINCT client_addr, datname FROM pg_stat_activity WHERE datname IN ('payments','ledger')"
```

## Пересборка сервиса

```powershell
docker build -t payments-api:dev -f src/Payments.Api/Dockerfile .
k3d image import payments-api:dev -c highload
kubectl -n highload rollout restart deployment/payments-api
```
Тег тот же, поэтому Deployment сам не заметит новый образ — нужен `rollout restart`.

## Удалить

```powershell
kubectl delete -k deploy/k8s     # всё, включая том Kafka
k3d cluster delete highload      # или весь кластер
```
