# Стенд в Kubernetes (k3d), этап 1

Четыре сервиса, Kafka, Redis и дашборд телеметрии — в кластере k3d; PostgreSQL — нативно на хосте (порт 5433), поды ходят к нему по имени `host.docker.internal`.

| Манифест | Что | Порт в кластере |
|---|---|---|
| `kafka.yaml` | Kafka 4.3.2, один узел KRaft, StatefulSet с томом | `kafka:9092` |
| `redis.yaml` | Redis без персистентности | `redis:6379` |
| `dashboard.yaml` | Aspire dashboard: UI и приём OTLP | `dashboard:18888`, `dashboard:18889` |
| `bank-emulator.yaml` | эмулятор банка (Go), одна реплика: решения в памяти | `bank-emulator:5300` |
| `antifraud.yaml` | антифрод (Python) | `antifraud:8000` |
| `payments-api.yaml` | Payments.Api, от 2 до 4 реплик на разных нодах | `payments-api:8080` |
| `ledger-service.yaml` | Ledger.Service | `ledger-service:8080` |
| `ingress.yaml` | вход через Traefik | `localhost:8080` снаружи |
| `traefik.yaml` | Traefik: таймаут подключения 1 с вместо 30 и повтор запроса на другом поде | — |
| `hpa.yaml` | HorizontalPodAutoscaler Payments.Api: 2–4 реплики по CPU | — |
| `pdb.yaml` | PodDisruptionBudget Payments.Api: при `kubectl drain` одна реплика всегда в работе | — |

## Запуск

1. **Кластер** (один раз):
   ```powershell
   k3d cluster create highload --agents 2 --api-port 127.0.0.1:6550 -p "8080:80@loadbalancer"
   kubectl -n kube-system scale deployment/coredns --replicas=2
   ```
   Порт API задан явно и ниже 49152. Без `--api-port` k3d берёт случайный порт из динамического диапазона, а Windows (WinNAT, Hyper-V) после перезагрузки резервирует в этом диапазоне свои блоки: если порт кластера попал в такой блок, балансировщик k3d не стартует с ошибкой `bind: An attempt was made to access a socket in a way forbidden by its access permissions`, и `kubectl` не подключается. Занятые блоки: `netsh interface ipv4 show excludedportrange protocol=tcp`. Вторая реплика CoreDNS — см. [ниже](#coredns-в-двух-экземплярах).

   После перезагрузки ноутбука или перезапуска Docker Desktop кластер сам не стартует: `k3d cluster start highload`.
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
   Заменить `CHANGE_ME` в обеих строках и создать из файла Secret:
   ```powershell
   kubectl create namespace highload
   kubectl -n highload create secret generic postgres --from-env-file=deploy/k8s/postgres.env
   ```
   Secret создаётся руками, а не из `kustomization.yaml`: кластер синхронизирует из git Argo CD, а пароля в git нет. Сменился пароль — пересоздать Secret и перезапустить поды:
   ```powershell
   kubectl -n highload create secret generic postgres --from-env-file=deploy/k8s/postgres.env --dry-run=client -o yaml | kubectl apply -f -
   kubectl -n highload rollout restart deployment/payments-api deployment/ledger-service
   ```
4. **Остановить стенд docker compose** — на два стенда сразу памяти не хватит:
   ```powershell
   docker compose down
   ```
5. **Развернуть:**
   ```powershell
   kubectl -n kube-system wait --for=condition=complete job/helm-install-traefik-crd job/helm-install-traefik --timeout=180s
   kubectl apply -k deploy/k8s
   kubectl -n highload get pods -w
   ```
   Первая команда нужна только на свежем кластере: k3s ставит Traefik и его типы ресурсов (`Middleware`, `ServersTransport` из `traefik.yaml`) через минуту после создания кластера, а без них `apply` падает с `no matches for kind "Middleware"`. Если она отвечает `not found` — задания ещё не созданы, повторить через несколько секунд.
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

## Выкатка новой версии

Кластер синхронизирует из git Argo CD (`deploy/argocd/highload.yaml`), поэтому новая версия выкатывается через git, а не через `kubectl`:
```powershell
git commit -am "..."                                   # код сервиса закоммичен
.\deploy\release.ps1 -Services payments-api             # сборка с тегом = коммит, импорт в ноды, тег в kustomization.yaml, push
kubectl -n highload rollout status deployment/payments-api
```
Argo CD замечает новый тег в течение 3 минут, сразу — по кнопке Refresh. Откат — `git revert` коммита `Release: ...` и `git push`: тег возвращается к прошлому, а прошлый образ ещё лежит в нодах.

Без скрипта, вручную — то же самое: `docker build -t payments-api:<тег>`, `k3d image import payments-api:<тег> -c highload`, тег в блок `images:` в `kustomization.yaml`, коммит, push.

## Удалить

```powershell
kubectl delete -k deploy/k8s     # всё, включая том Kafka
k3d cluster delete highload      # или весь кластер
```

## CoreDNS в двух экземплярах

k3s запускает CoreDNS в одном экземпляре. Если упадёт нода, на которой он живёт, в кластере пропадёт DNS, и ни один новый под не стартует: Payments.Api и Ledger.Service при старте резолвят `host.docker.internal`, `kafka` и `redis`. Вторая реплика — на другой ноде:
```powershell
kubectl -n kube-system scale deployment/coredns --replicas=2
kubectl -n kube-system get pods -l k8s-app=kube-dns -o wide     # реплики должны быть на разных нодах
```
CoreDNS — системный компонент k3s, не часть `deploy/k8s`; после перезапуска сервера k3s может вернуть одну реплику — тогда команду повторить.

## Мониторинг

Prometheus и Grafana (чарт kube-prometheus-stack) ставит Argo CD: `deploy/argocd/monitoring.yaml`, настройки — `deploy/monitoring/values.yaml`. Пароль администратора Grafana — в Secret, который создаётся руками до установки, как пароль Postgres:
```powershell
kubectl create namespace monitoring
kubectl -n monitoring create secret generic grafana-admin --from-literal=admin-user=admin --from-literal=admin-password='<пароль>'
kubectl apply -f deploy/argocd/monitoring.yaml
```
Grafana наружу не опубликована:
```powershell
kubectl -n monitoring port-forward svc/monitoring-grafana 3000:80     # http://localhost:3000, admin и пароль из Secret
```

