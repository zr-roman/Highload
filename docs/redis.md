# Redis в кластере: три копии и Sentinel

В Redis стенда лежат лимиты мерчантов Payments.Api (token bucket) и счётчики и решения antifraud. Потерять их не страшно: Payments.Api при недоступном Redis переходит на локальный лимит в памяти процесса (`LocalMerchantLimiter`), antifraud отвечает 503, и платёж идёт по своей логике повторов. Поэтому Redis без персистентности. Но одна копия была единственной точкой отказа: с ней умирала нода, и инструмент опытов [load/Failover](../load/Failover) каждый раз переносил её заранее, чтобы она не заслоняла сам опыт.

## Как устроено

- **StatefulSet из трёх копий, по одной на ноду** (жёсткая anti-affinity): мастер и две реплики с асинхронной репликацией. PodDisruptionBudget `maxUnavailable: 1`, так что drain выселяет копии по одной.
- **Sentinel рядом с каждой копией**, в том же поде. Мастер, который молчит 5 секунд, считается упавшим, если с этим согласны двое из трёх Sentinel. Тогда они повышают одну из реплик.
- **Клиенты спрашивают у Sentinel, где мастер.** Payments.Api: `serviceName=mymaster` в строке подключения StackExchange.Redis, клиент сам переключается после failover. Antifraud: `ANTIFRAUD_REDIS_SENTINELS`, `redis.asyncio.sentinel`, мастер запрашивается заново на каждое новое соединение. В docker-compose остаётся один Redis по `ANTIFRAUD_REDIS_URL`.
- **Имена вместо IP.** Headless Service `redis` даёт копиям имена `redis-0.redis` … `redis-2.redis`. Redis и Sentinel объявляют себя по ним (`replica-announce-ip`, `announce-hostnames`), потому что IP пода меняется при каждом перезапуске.
- **Кто мастер при старте.** Init-контейнер сначала спрашивает Sentinel других копий. После failover мастер — тот, кого они выбрали, и вернувшаяся копия встаёт к нему репликой. Если не ответил никто (весь набор стартует с нуля), мастером становится `redis-0`: StatefulSet запускает его первым.
- **Restricted:** не root, корень только для чтения, конфиги и снимки репликации — в `emptyDir`.

## Решения и цена

| Решение | Почему | Цена |
|---|---|---|
| Sentinel, а не Redis Cluster | Данных мало, шардировать нечего; нужна только замена мастера | Пишет один мастер: запись не масштабируется |
| Асинхронная репликация | Потеря последних инкрементов лимита или счётчика не опасна | При failover пропадает то, что мастер не успел отправить |
| Без оператора | Три пода и короткий скрипт понятнее ещё одного CRD | Обновление версии и смену топологии делаем руками |
| Sentinel в поде Redis | Три кворумных голоса без лишних подов | Нода уносит и копию, и её голос; кворум 2 из 3 это переносит |

## Как проверить

```powershell
kubectl -n highload get pods -l app.kubernetes.io/name=redis -o wide
kubectl -n highload exec redis-0 -c sentinel -- redis-cli -p 26379 sentinel get-master-addr-by-name mymaster
kubectl -n highload exec redis-0 -c redis -- redis-cli info replication

# Отказ мастера под нагрузкой: удалить под мастера и смотреть, кого выберут
$m = (kubectl -n highload exec redis-0 -c sentinel -- redis-cli -p 26379 sentinel get-master-addr-by-name mymaster)[0].Split('.')[0]
kubectl -n highload delete pod $m
kubectl -n highload exec redis-1 -c sentinel -- redis-cli -p 26379 sentinel get-master-addr-by-name mymaster
```
