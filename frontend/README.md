# React-фронтенд платёжной платформы

Интерфейс на React и TypeScript для ручной проверки сценариев первой версии. Состав и поведение сценариев описаны в [`docs/frontend-scope.md`](../docs/frontend-scope.md).

## Требования

- Node.js и npm.

## Локальный запуск (PowerShell)

```powershell
Copy-Item .env.example .env.local
npm ci
npm run dev
```

Vite выведет адрес локального dev-сервера на порту 5173. По умолчанию Vite проксирует `/payments` и `/balances` через Traefik на `VITE_API_PROXY_TARGET` (`http://localhost:8080`), чтобы локальная разработка не зависела от CORS-настроек уже запущенных API. Если нужно обращаться к API из браузера напрямую, заполните `VITE_PAYMENTS_API_URL` и `VITE_LEDGER_API_URL`; локальные API разрешают origin Vite dev-сервера из CORS-конфигурации. Файл `.env.local` не добавляется в Git.

## Production-сборка

```powershell
npm ci
npm run build
npm run preview
```

Сборка создаётся в `dist/`. Значения `VITE_*` подставляются во время сборки; секреты в них хранить нельзя.

## Сквозной тест в pull request

При открытии, повторном открытии и обновлении любого PR Frontend CI запускает Chromium на временном стенде Docker Compose внутри GitHub Actions. Стенд собирает React, Payments API, Ledger и эмулятор банка из версии PR. PostgreSQL, Kafka и Redis получают отдельные данные для каждого запуска.

Playwright создаёт платёж на 10 ₽, находит его по UUID и проверяет зачисление суммы за вычетом комиссии. Результат входит в обязательную для успешного `Frontend checks` проверку `Browser end-to-end test`. При ошибке доступны логи контейнеров в этой задаче. После прогона CI удаляет стенд вместе с томами.

Чтобы повторить этот прогон из корня проекта в PowerShell с запущенным Docker Desktop:

```powershell
docker compose -f frontend/e2e/compose.yaml up --build --wait --wait-timeout 240
npm --prefix frontend ci
cd frontend
npx playwright install chromium
$env:E2E_BASE_URL = "http://127.0.0.1:18080"
$env:E2E_EXPECT_CAPTURED = "true"
npm run test:e2e
Remove-Item Env:E2E_EXPECT_CAPTURED
Remove-Item Env:E2E_BASE_URL
cd ..
docker compose -f frontend/e2e/compose.yaml down --volumes --remove-orphans
```

## Ручной запуск на стенде Kubernetes

Сквозной тест отправляет настоящий платёж и запрашивает баланс в указанном стенде. Он создаёт данные в его базах, поэтому запускайте его только против тестового кластера, не против рабочего или общего демонстрационного стенда.

```powershell
npx playwright install chromium
$env:E2E_BASE_URL = "http://localhost:8080"
npm run test:e2e
Remove-Item Env:E2E_BASE_URL
```

Для ручной проверки уже развёрнутой версии через GitHub Actions укажите доступный раннеру адрес тестового стенда в поле `e2e_base_url` в `Frontend CI` → `Run workflow`.

Docker-образ собирается из корня репозитория командой `docker build -t frontend:dev -f frontend/Dockerfile .`. В Kubernetes его обслуживают две реплики непривилегированного NGINX; Traefik направляет `/` на React, а `/payments` и `/balances` — на API. Весь стенд доступен по `http://localhost:8080`, браузер обращается к UI и API на одном origin, поэтому отдельное правило CORS для этого сценария не требуется. В CI образ публикуется в `ghcr.io/zr-roman/frontend:<коммит>`, а Argo CD выкатывает его после обновления тега в Kustomize.
