# React-фронтенд платёжной платформы

Заготовка интерфейса на React и TypeScript. Сценарии первой версии описаны в [`docs/frontend-scope.md`](../docs/frontend-scope.md).

## Требования

- Node.js и npm.

## Локальный запуск (PowerShell)

```powershell
Copy-Item .env.example .env.local
npm ci
npm run dev
```

Vite выведет адрес локального dev-сервера. Адреса Payments API и Ledger можно изменить в `.env.local`; этот файл не добавляется в Git.

## Production-сборка

```powershell
npm ci
npm run build
npm run preview
```

Сборка создаётся в `dist/`. Значения `VITE_*` подставляются во время сборки; секреты в них хранить нельзя.
