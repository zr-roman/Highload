# GitOps: стенд в k3d под Argo CD

Что работает в кластере — то, что лежит в `deploy/k8s` на ветке `main` в GitHub. Изменения доходят до кластера через `git push`, а не через `kubectl apply`; откат — `git revert`. Кластер, манифесты и замеры — в [deploy/k8s/README.md](../deploy/k8s/README.md) и [load-testing.md](load-testing.md#стенд-в-kubernetes-k3d).

## Как устроено

```
 git push ──► GitHub: deploy/k8s на main
                  │  Argo CD читает раз в 3 минуты, сразу — по Refresh
                  ▼
             Argo CD (namespace argocd)
                  │  kustomize build deploy/k8s, server-side apply
                  ▼
             namespace highload: сервисы, Kafka, Redis, Traefik-маршруты, PDB, HPA
```

- **Приложение** — [deploy/argocd/highload.yaml](../deploy/argocd/highload.yaml). Применяется руками один раз и лежит вне `deploy/k8s`: иначе приложение управляло бы само собой, и удаление его из git удалило бы его из кластера.
- **`automated` с `prune` и `selfHeal`.** Удалённое из git удаляется из кластера; правка руками (`kubectl scale`, `edit`) откатывается к тому, что в git.
- **Kustomize, без Helm.** `deploy/k8s` уже был kustomization: Argo CD собирает его сам.
- **Argo CD без dex** (`argocd-dex-server` — 0 реплик): SSO на стенде не нужен, а память на ноутбуке на счету.

## Решения и почему

| Что | Почему |
|---|---|
| Пароль Postgres — Secret `postgres`, созданный руками, а не `secretGenerator` | Argo CD собирает манифесты из git, а `postgres.env` с паролем в git нет: с генератором сборка падает. Цена — при смене пароля поды перезапускаются вручную, хеш в имени Secret больше не делает этого сам |
| `ServerSideApply=true` | Поля, которые API-сервер дописывает по умолчанию (`volumeClaimTemplates` у StatefulSet Kafka), не считаются разницей. Без этого приложение вечно `OutOfSync` — то же `statefulset.apps/kafka configured` на каждом `kubectl apply`, что было весь этап 2 |
| `replicas` нет в Deployment Payments.Api, `ignoreDifferences` для `/spec/replicas` | Числом реплик управляет HPA. Число в манифесте Argo CD возвращал бы при каждой синхронизации, споря с HPA |
| Тег образа = короткий хеш коммита, в блоке `images:` в `kustomization.yaml` | С тегом `:dev` новый образ для Kubernetes и Argo CD неотличим от старого, и новая версия не выкатывается. Новый тег в git — это и есть новая версия |

## Выкатка новой версии

[deploy/release.ps1](../deploy/release.ps1) делает на стенде то, что в продакшене делают CI и реестр образов:

1. Отказывается работать, если исходники сервиса не закоммичены: тег назвал бы коммит, которому образ не соответствует.
2. `docker build` с тегом = HEAD.
3. `k3d image import` — **до** того, как тег попадёт в git: Argo CD может выкатить сразу, и поды должны найти образ.
4. Тег в `kustomization.yaml`, коммит **только этого файла** и push.
5. Нажимает Refresh у приложения (аннотация `argocd.argoproj.io/refresh=normal`), ждёт, пока Argo CD синхронизирует **этот** коммит, потом — `rollout status` каждого сервиса. `Released:` печатается, когда новая версия работает в кластере, а не когда коммит ушёл на GitHub.

```powershell
.\deploy\release.ps1 -Services payments-api
.\deploy\release.ps1 -Services payments-api,ledger-service
```

Выкатка идёт под всем, что настроено на этапе 2: readiness, пауза `preStop`, PDB.

## Что проверено

| Демонстрация | Результат |
|---|---|
| Изменение через git: `maxReplicas` HPA 4 → 3, push | Через Refresh — `MAXPODS 3` в кластере |
| Откат: `git revert` того коммита, push | `MAXPODS 4`; в истории Argo CD обе синхронизации с коммитами |
| Дрейф: `kubectl scale deployment/redis --replicas=0` | `selfHeal` возвращает одну реплику |
| Релиз скриптом: `payments-api`, затем `ledger-service` | Поды на `payments-api:d8882dc86ba0` и `ledger-service:c1ae1c4b9a25`. Скрипт дождался синхронизации коммита и выкатки |

Тег — это версия исходников, а не содержимое образа: код Payments.Api между `:dev` и `:d8882dc86ba0` не менялся, и образ получил тот же sha256 — сборка воспроизводима, Docker взял всё из кэша. Поды всё равно перезапустились: для Kubernetes сменилось имя образа в спецификации.

## Чему научили ошибки

- **`git commit -am` закоммитил чужое.** Ключ `-a` берёт все изменённые файлы — так в коммит «HPA до 3 реплик» попала незаконченная работа над отменой авторизации в `bank.go`, а `git revert` того коммита убрал её и из рабочей копии. Работа восстановлена из коммита, но вывод — коммитить явный список файлов. Поэтому `release.ps1` коммитит `git commit -- deploy/k8s/kustomization.yaml`, что бы ни лежало в рабочей копии.
- **`rollout status` сразу после push отвечает про прошлую выкатку.** Argo CD ещё не видел коммит — отсюда ожидание синхронизации конкретной ревизии в скрипте.
- **PowerShell: результат цикла — всё, что он вывел.** Лог `docker build` оказался в списке образов, а один образ без `@(...)` — строкой, которую `@images` передал в k3d по буквам. Лог — через `Out-Host`, список — всегда массив.

## Что на стенде упрощено

| На стенде | В продакшене |
|---|---|
| Образы собираются на ноутбуке и загружаются в ноды `k3d image import` | CI собирает при push, образы в реестре, ноды скачивают сами |
| Тег в `kustomization.yaml` меняет скрипт и коммитит в `main` | CI открывает pull request в репозиторий с манифестами или Argo CD Image Updater пишет тег сам |
| Secret с паролем создан руками | Sealed Secrets или External Secrets: в git — зашифрованный Secret или ссылка на хранилище секретов |
| Одна ветка, одно окружение | Отдельные пути или репозитории для окружений, продвижение версии из staging в prod через git |
| `admin` с начальным паролем | SSO через dex и RBAC проектов Argo CD |
