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

В Kubernetes приложение должно открываться через общий входной адрес с API-маршрутами `/payments` и `/balances`; если UI и API находятся на одном origin, отдельное правило CORS не требуется. Для другого адреса стенда укажите `VITE_PAYMENTS_API_URL` и `VITE_LEDGER_API_URL` при сборке и внесите origin интерфейса в `Cors:AllowedOrigins` API.
