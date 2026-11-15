# Безопасность стенда в k3d

Что сделано, чтобы взлом одного сервиса не давал доступа ко всему стенду, а уязвимый образ или подменённая зависимость не доходили до кластера. Стенд учебный и живёт на ноутбуке, но каждая мера — та же, что в продакшене, и проверена в кластере. Секреты — в [gitops.md](gitops.md) (Sealed Secrets), выкатка — там же (CI).

## Что сделано

| Слой | Мера | Где |
|---|---|---|
| Сеть | Внутри `highload` всё закрыто, открыто только нужное (NetworkPolicy) | [deploy/k8s/network-policies.yaml](../deploy/k8s/network-policies.yaml) |
| Под | Не root, без capabilities, без повышения привилегий, seccomp, корневая ФС только для чтения | `securityContext` в манифестах `deploy/k8s/*.yaml` |
| Namespace | API-сервер отказывает поду, нарушающему профиль `restricted` (Pod Security Admission) | [deploy/k8s/namespace.yaml](../deploy/k8s/namespace.yaml) |
| Образ | Trivy проверяет образ до push в ghcr.io: критическая уязвимость с исправлением останавливает релиз | [.github/workflows/ci.yml](../.github/workflows/ci.yml) |
| Зависимости | Dependabot: actions, NuGet, uv, базовые образы — раз в неделю, не раньше 7 дней после выхода версии | [.github/dependabot.yml](../.github/dependabot.yml) |
| Секреты | Пароль PostgreSQL генерирует оператор в кластере и в git его нет; пароль Grafana в git только зашифрованным (Sealed Secrets) | [gitops.md](gitops.md) |

## Сеть: NetworkPolicy

Без политик любой под в namespace подключается к любому: взломанный antifraud мог бы читать Kafka или вызывать банк от имени Payments.Api. Сначала закрыто всё (`default-deny-ingress`), потом открыто то, что нужно по архитектуре:

| Куда | Кому можно |
|---|---|
| payments-api, ledger-service, dashboard UI | Traefik из `kube-system` |
| redis | payments-api, antifraud |
| kafka | payments-api, ledger-service |
| bank-emulator, antifraud | payments-api |
| otel-collector | payments-api, ledger-service |
| dashboard (OTLP) | otel-collector |

k3s применяет NetworkPolicy сам: в него встроен контроллер kube-router. Пробы kubelet приходят с ноды и проходят без отдельного правила; `kubectl port-forward` и `exec` идут через kubelet, а не через сеть подов.

**Проверено:** временный под без метки не подключился к Redis (`nc` — таймаут), тот же под с меткой `app.kubernetes.io/name=antifraud` — подключился. Нагрузка 100 платежей в секунду — без ошибок.

**Упрощение:** закрыт только вход. Исходящие открыты: Postgres живёт на хосте, и список разрешённых исходящих требовал бы его адреса, DNS и API-сервера. Хранилища, которые важны, закрыты на входе.

## Под: securityContext и Pod Security Admission

У всех восьми подов `highload`:

- `runAsNonRoot: true` — kubelet не запустит контейнер от root;
- `seccompProfile: RuntimeDefault` — стандартный фильтр системных вызовов среды выполнения;
- `allowPrivilegeEscalation: false` — процесс не получит больше прав, чем при старте (setuid-файлы не помогут);
- `capabilities: drop: [ALL]` — ни одной привилегии ядра; сервисам на портах выше 1024 они не нужны;
- `readOnlyRootFilesystem: true` — у всех, кроме Kafka: её образ при старте пишет конфиг брокера в `/opt/kafka/config`. Тем, кому нужно куда-то писать (.NET-сервисы, antifraud, dashboard), дан `/tmp` в памяти, до 64 МиБ.

Пользователь задан числом там, где образ называет его по имени или стартует от root: по имени Kubernetes не может проверить, что это не root, и отказывается запускать под.

| Сервис | Пользователь | Почему |
|---|---|---|
| payments-api, ledger-service | 1654 из образа | `aspnet:10.0-noble-chiseled` уже без root |
| bank-emulator | 65532 из образа | `distroless/static:nonroot` |
| antifraud | 10001 | `USER antifraud` в Dockerfile |
| redis | 999:1000 | образ стартует от root и сам переключается на `redis` — здесь сразу `redis` |
| kafka | 1000:1000 | `appuser` образа, владелец файлов на томе данных |
| dashboard | 1654 | `app` в .NET-образах |

На namespace стоит `pod-security.kubernetes.io/enforce: restricted`: даже если настройки уберут из манифеста, API-сервер такой под не создаст.

**Проверено:** все поды `1/1`; Redis работает как `uid=999(redis)`, запись в корень — `Read-only file system`; под busybox от root отклонён: `violates PodSecurity "restricted:latest"` со списком нарушений.

## Образы и зависимости

**Trivy в CI.** Образ собирается в локальный Docker раннера, сканируется и только потом уходит в ghcr.io. В лог — уязвимости HIGH и CRITICAL, у которых есть исправление; критическая с исправлением останавливает релиз. Уязвимость без исправления релиз не останавливает: сделать с ней пока нечего.

Первые результаты по уже опубликованным образам:

| Образ | База | Пакетов ОС | HIGH / CRITICAL |
|---|---|---|---|
| ledger-service | `aspnet:10.0-noble-chiseled` | 8 | 0 / 0 |
| antifraud | `python:3.12-slim` (Debian 13) | 87 | 2 / 0 — zlib, исправлено в Debian |

Разница — в базовом образе: в chiseled-образе нет ни shell, ни пакетного менеджера, только то, что нужно для запуска .NET, — 8 пакетов против 87, и уязвимостям меньше где появиться. Уязвимость zlib у antifraud уйдёт со следующей сборкой: релиз пересобирает образ на свежем `python:3.12-slim`.

**Закреплённые версии.** Trivy запускается из образа `aquasec/trivy` фиксированной версии, а не через `trivy-action` и не `latest`: в марте 2026 теги `trivy-action` и образ `latest` подменили на версии, крадущие секреты CI. Закреплённая версия и проверенный релиз — защита от этого класса атак; обновлять её — осознанным коммитом.

**Dependabot.** Раз в неделю pull request'ы с новыми версиями: GitHub Actions, NuGet (`Directory.Packages.props`), uv (`uv.lock` antifraud), базовые образы Dockerfile и образы в манифестах. Каждый проходит тесты CI. Новую версию Dependabot предлагает не раньше чем через 7 дней после выхода (`cooldown`): подменённые пакеты обычно находят и отзывают раньше.

## Что упрощено по сравнению с продакшеном

| На стенде | В продакшене |
|---|---|
| NetworkPolicy только на вход | И на выход: сервис ходит только туда, куда должен |
| Образы не подписаны | Подпись (cosign) в CI и проверка подписи при допуске пода |
| Нет SBOM | SBOM каждого образа: по нему отвечают на «где у нас уязвимая версия X» за минуты |
| Pod Security Admission | Плюс политики (Kyverno, Gatekeeper): только образы из своего реестра, обязательные лимиты, запрет `latest` |
| Трафик между подами без шифрования | mTLS через service mesh |
| Базовые образы по тегу | По digest, обновляет Dependabot |
| Нет наблюдения за поведением процессов | Runtime-защита (Falco): оболочка в контейнере, неожиданный сетевой вызов |

## Как проверить

```powershell
# Политики и поды
kubectl -n highload get networkpolicy
kubectl -n highload get pods

# Сеть: без метки к Redis не пустит, с меткой antifraud — пустит. Под сам соответствует restricted, иначе его не пустит admission.
$sc = '{"spec":{"securityContext":{"runAsNonRoot":true,"runAsUser":65534,"seccompProfile":{"type":"RuntimeDefault"}},"containers":[{"name":"np-test","image":"busybox:1.37","command":["nc","-zv","-w","3","redis","6379"],"securityContext":{"allowPrivilegeEscalation":false,"capabilities":{"drop":["ALL"]}}}]}}'
kubectl -n highload run np-test --rm -it --restart=Never --image=busybox:1.37 --overrides=$sc
kubectl -n highload run np-test --rm -it --restart=Never --image=busybox:1.37 --labels=app.kubernetes.io/name=antifraud --overrides=$sc

# Под: не root, корень только для чтения, admission отклоняет root
kubectl -n highload exec deploy/redis -- id
kubectl -n highload exec deploy/redis -- touch /x
kubectl -n highload run psa-test --rm -it --restart=Never --image=busybox:1.37 -- id

# Образ: то же сканирование, что в CI
docker run --rm aquasec/trivy:0.72.0 image --severity HIGH,CRITICAL --ignore-unfixed --no-progress ghcr.io/zr-roman/ledger-service:<тег>
```
