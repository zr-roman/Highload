# Эмулятор банка (Go)

Первый сервис проекта на Go. Он заменит `FakeAcquirer` внутри Payments.Api настоящим сетевым вызовом: с задержкой, отказами и медленными ответами. На нём Payments.Api отработает таймауты, ретраи и circuit breaker — это следующий шаг этапа 2.

Только стандартная библиотека Go, без внешних пакетов.

## API

**`POST /v1/authorizations`** — решение по платежу.

```json
{"paymentId": "0192f0c8-0000-7000-8000-000000000001", "merchantId": 42, "amount": 150000, "currency": "RUB"}
```

```json
{"paymentId": "0192f0c8-0000-7000-8000-000000000001", "status": "approved", "authorizationCode": "798041", "decidedAt": "2026-10-21T20:09:32.287Z"}
```

`status` — `approved` или `declined`; код авторизации — только у одобренных.

**`GET /v1/authorizations/{paymentId}`** — уже принятое решение: 200 с тем же JSON или 404, если решения нет. Понадобится задаче восстановления зависших платежей: она спросит банк, чем кончился платёж, ответ на который не дошёл.

**`GET /healthz`** — 200 и `ok`.

| Код | Когда |
|---|---|
| 200 | решение принято сейчас или раньше |
| 400 | не JSON или неверные поля: пустой `paymentId`, `merchantId` или `amount` ≤ 0, `currency` не из трёх букв |
| 404 | `GET`: решения по этому платежу нет |
| 409 | этот `paymentId` уже был с другими полями |
| 500 | имитация сбоя до решения |

## Поведение

**Идемпотентность по `paymentId`.** Повторный запрос с тем же `paymentId` получает то же решение — это и делает ретраи в Payments.Api безопасными. Тот же `paymentId` с другими полями — ошибка клиента, 409.

**Настройки** — переменные окружения:

| Переменная | По умолчанию | Что делает |
|---|---|---|
| `BANK_ADDR` | `:5300` | адрес и порт |
| `BANK_LATENCY_MS` | 50 | время обработки платежа |
| `BANK_JITTER_MS` | 20 | к нему добавляется случайная часть до стольких миллисекунд |
| `BANK_DECLINE_PERCENT` | 2 | доля отклонённых платежей |
| `BANK_ERROR_PERCENT` | 0 | доля ответов 500 до какого-либо решения |
| `BANK_SLOW_PERCENT` | 0 | доля поздних ответов: решение уже принято и сохранено, а ответ задерживается |
| `BANK_SLOW_MS` | 5000 | на сколько задерживается поздний ответ |

**Три вида сбоев — три разных случая для клиента:**

1. **Ошибка до решения** (`BANK_ERROR_PERCENT`). Банк ничего не решил, повтор безопасен.
2. **Поздний ответ** (`BANK_SLOW_PERCENT`). Банк решил и запомнил, но клиент не дождался ответа и не знает исхода. Самый опасный случай: повторить запрос с тем же `paymentId` можно — придёт сохранённое решение, а нового списания не будет. Без идемпотентности банка он превратился бы в двойное списание.
3. **Клиент ушёл во время обработки.** Банк прекращает работу над запросом и ничего не решает.

## Задание

Готово: `main.go`, `config.go`, типы, маршруты, JSON-хелперы и тесты. **Тесты — это спецификация:** задание выполнено, когда все они зелёные. Написать нужно восемь функций с пометкой `TODO`, в каждой — подсказки:

- `bank.go`: `chance`, `processingDelay`, `Authorize`, `Get`;
- `handlers.go`: `validate`, `sleep`, `handleGet`, `handleAuthorize`.

Удобный порядок — как в списке: сначала логика банка, потом HTTP. Запускай только нужные тесты, чтобы видеть прогресс:

```powershell
cd src\bank-emulator
go test ./...                                    # все тесты
go test -run 'Chance|ProcessingDelay|TestAuthorize|TestGet$|Concurrent' -v ./...   # только логика банка, подробно
go vet ./...                                     # частые ошибки, которые компилятор пропускает
```

`go test -race ./...` дополнительно ловит гонки данных, но на Windows ему нужен компилятор C (gcc). Без него тест на конкурентные запросы проверит только результат.

Сейчас проходят 4 теста из 19: конфигурация и `/healthz`. Когда пройдут все и `go vet` будет молчать — присылай код на ревью.

VS Code с расширением Go сам добавляет недостающие импорты при сохранении файла: например, `fmt` для `fmt.Sprintf` или `errors` для `errors.Is`.

## Запуск

```powershell
cd src\bank-emulator
go run .
```

Во втором окне:

```powershell
$body = '{"paymentId":"0192f0c8-0000-7000-8000-000000000001","merchantId":42,"amount":150000,"currency":"RUB"}'
Invoke-RestMethod -Method Post -Uri http://localhost:5300/v1/authorizations -ContentType "application/json" -Body $body
Invoke-RestMethod http://localhost:5300/v1/authorizations/0192f0c8-0000-7000-8000-000000000001
```

Ненадёжный банк: 10 % отказов и 5 % поздних ответов.

```powershell
$env:BANK_ERROR_PERCENT = "10"
$env:BANK_SLOW_PERCENT = "5"
go run .
```

Ctrl+C останавливает сервер аккуратно: он перестаёт принимать соединения и дожидается запросов в работе.

## Go для C#-разработчика

| C# | Go |
|---|---|
| класс с методами | `struct` и методы с получателем: `func (b *Bank) Get(...)` |
| исключения, `try`/`catch` | ошибка — последнее возвращаемое значение: `if err != nil { ... }`; вместо `catch` по типу — `errors.Is` |
| `lock (obj)` | `sync.Mutex`: `mu.Lock()`, затем `defer mu.Unlock()` |
| `Dictionary<K, V>`, `TryGetValue` | `map[K]V`, `v, ok := m[key]` |
| `async`/`await`, `Task` | горутины (`go f()`) и обычные блокирующие вызовы: ждёт горутина, а не поток ОС |
| `CancellationToken` | `context.Context`; отмена — канал `ctx.Done()`, который закрывается |
| `await Task.Delay(d, token)` | `select` по `timer.C` и `ctx.Done()` |
| `using`, `finally` | `defer` |
| `app.MapPost("/x", ...)` в minimal API | `mux.HandleFunc("POST /x", ...)` (Go 1.22+) |
| атрибуты System.Text.Json | теги полей: `` `json:"paymentId"` `` |
| `class Bank : IBank` | интерфейсы реализуются неявно: есть нужные методы — значит, реализует |
| xUnit `[Fact]` | `func TestXxx(t *testing.T)` в файле `*_test.go`; `t.Errorf`, `t.Fatalf` |

## Что почитать

- [A Tour of Go](https://go.dev/tour/) — синтаксис за 2–3 часа. Обязательно разделы Methods and interfaces и Concurrency.
- [Go by Example](https://gobyexample.com/) — короткие примеры: Structs, Methods, Maps, Errors, Goroutines, Select, Timers, Mutexes, JSON, HTTP Server, Context, Testing.
- [Effective Go](https://go.dev/doc/effective_go) — когда освоишься: как пишут на Go принято.
