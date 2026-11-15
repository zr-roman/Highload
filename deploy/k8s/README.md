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
| `otel-collector.yaml` | OpenTelemetry Collector: телеметрия сервисов — метрики в Prometheus, всё в Aspire dashboard | `otel-collector:4317` |
| `dashboards/` | дашборды Grafana «Платежи» и «Устойчивость»: JSON, из которого kustomize делает ConfigMap | — |
| `monitors.yaml` | что ещё собирает Prometheus: Traefik и Argo CD | — |
| `alerts.yaml` | правила алертов Prometheus | — |
| `postgres-sealed.yaml` | строки подключения к Postgres с паролем, зашифрованные (SealedSecret) | — |
| `pdb.yaml` | PodDisruptionBudget Payments.Api: при `kubectl drain` одна реплика всегда в работе | — |

## Запуск

1. **Кластер** (один раз):
   ```powershell
   k3d cluster create highload --agents 2 --api-port 127.0.0.1:6550 -p "8080:80@loadbalancer" `
     --k3s-arg "--kube-controller-manager-arg=node-monitor-grace-period=20s@server:0"
   kubectl -n kube-system scale deployment/coredns --replicas=2
   ```
   Порт API задан явно и ниже 49152. Без `--api-port` k3d берёт случайный порт из динамического диапазона, а Windows (WinNAT, Hyper-V) после перезагрузки резервирует в этом диапазоне свои блоки: если порт кластера попал в такой блок, балансировщик k3d не стартует с ошибкой `bind: An attempt was made to access a socket in a way forbidden by its access permissions`, и `kubectl` не подключается. Занятые блоки: `netsh interface ipv4 show excludedportrange protocol=tcp`. Вторая реплика CoreDNS — см. [ниже](#coredns-в-двух-экземплярах).

   `node-monitor-grace-period=20s` вместо 50 с по умолчанию: остановленную ноду Kubernetes замечает за ~15 с, а не за ~50, и Traefik раньше перестаёт слать запросы в поды на ней ([опыт](../../docs/observability.md#время-обнаружения-отказа-50-с--20-с)). Цена: нода, которая молчит дольше 20 с, считается мёртвой, и поды с неё выселяются — так бывает и при перезапуске контейнера server-0. На уже созданном кластере:
   ```powershell
   docker exec k3d-highload-server-0 sh -c "printf 'kube-controller-manager-arg:\n  - node-monitor-grace-period=20s\n' > /etc/rancher/k3s/config.yaml"
   docker restart k3d-highload-server-0
   ```
   Файл живёт в контейнере ноды: `k3d cluster delete` его стирает.

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
3. **Пароли** лежат в git зашифрованными: `postgres-sealed.yaml` здесь и `deploy/monitoring/secrets/grafana-admin-sealed.yaml` — это SealedSecret. Открыть их может только контроллер Sealed Secrets в кластере (`deploy/argocd/sealed-secrets.yaml`, ставит Argo CD): у него закрытый ключ, а `kubeseal` на ноутбуке шифрует открытым. Контроллер нужен раньше, чем `kubectl apply -k`: без него тип `SealedSecret` кластеру неизвестен.

   **kubeseal** (один раз):
   ```powershell
   $v = "0.41.2"
   Invoke-WebRequest "https://github.com/bitnami/sealed-secrets/releases/download/v$v/kubeseal-$v-windows-amd64.tar.gz" -OutFile "$env:TEMP\kubeseal.tar.gz"
   New-Item -ItemType Directory -Force "$HOME\bin" | Out-Null
   tar -xzf "$env:TEMP\kubeseal.tar.gz" -C "$HOME\bin" kubeseal.exe
   [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "User") + ";$HOME\bin", "User")
   ```

   **Зашифровать заново** — на новом кластере (у его контроллера новый ключ, старые файлы он не откроет) или при смене пароля. Строки подключения с паролем — в `postgres.env`, его нет в git:
   ```powershell
   Copy-Item deploy\k8s\postgres.env.example deploy\k8s\postgres.env   # и заменить CHANGE_ME в обеих строках
   kubectl -n highload create secret generic postgres --from-env-file=deploy/k8s/postgres.env --dry-run=client -o yaml |
       kubeseal -o yaml > deploy/k8s/postgres-sealed.yaml
   kubectl -n monitoring create secret generic grafana-admin --from-literal=admin-user=admin --from-literal=admin-password='<пароль>' --dry-run=client -o yaml |
       kubeseal -o yaml > deploy/monitoring/secrets/grafana-admin-sealed.yaml
   ```
   Затем коммит и push: Argo CD применит SealedSecret, контроллер обновит Secret. Поды читают пароль из переменных окружения только при старте, поэтому после смены пароля: `kubectl -n highload rollout restart deployment/payments-api deployment/ledger-service`.

   **Ключ контроллера** — единственное, чего нет в git, и без него файлы в git не открыть. Сохранить вне репозитория (менеджер паролей):
   ```powershell
   kubectl -n kube-system get secret -l sealedsecrets.bitnami.com/sealed-secrets-key -o yaml > "$HOME\sealed-secrets-key.yaml"
   ```
   Восстановить на новом кластере: `kubectl apply -f "$HOME\sealed-secrets-key.yaml"`, затем `kubectl -n kube-system rollout restart deployment/sealed-secrets-controller` — контроллер читает ключи при старте; шифровать заново не нужно. Контроллер выпускает новый ключ раз в 30 дней (старые хранит и ими же открывает старые файлы): сохранять ключи стоит заново после каждого нового.

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

Prometheus и Grafana (чарт kube-prometheus-stack) ставит Argo CD: `deploy/argocd/monitoring.yaml`, настройки — `deploy/monitoring/values.yaml`. Пароль администратора Grafana — SealedSecret `deploy/monitoring/secrets/grafana-admin-sealed.yaml`, третий источник приложения `monitoring` (как его зашифровать — шаг 3 выше):
```powershell
kubectl apply -f deploy/argocd/monitoring.yaml
```
Дашборды стенда — «Highload · Платежи» и «Highload · Устойчивость» — лежат в `deploy/k8s/dashboards/` и попадают в Grafana сами. В интерфейсе Grafana их можно менять, но не сохранять: изменённый дашборд — Export → JSON → файл в `dashboards/` → коммит.

Grafana наружу не опубликована:
```powershell
kubectl -n monitoring port-forward svc/monitoring-grafana 3000:80     # http://localhost:3000, admin и пароль из Secret
```

Алерты — `deploy/k8s/alerts.yaml` (PrometheusRule): недоступный под Payments.Api, 5xx клиентам, застрявший outbox, нода NotReady, CrashLoopBackOff. Alertmanager без получателей: сработавшие алерты видны в его интерфейсе и в Grafana (Alerting → Alert rules, источник Alertmanager).
```powershell
kubectl -n monitoring port-forward svc/monitoring-alertmanager 9093:9093    # http://localhost:9093
kubectl -n monitoring port-forward svc/monitoring-prometheus 9090:9090      # http://localhost:9090/alerts — правила и их состояние
```

Что на дашбордах, почему мониторинг закреплён за server-0 и что показали опыты с отказом нод — в [docs/observability.md](../../docs/observability.md).

