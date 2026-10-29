using StackExchange.Redis;

namespace Payments.Api.RateLimiting;

public sealed class RedisTokenBucket(IDatabase db)
{
    public async Task<RateLimitDecision> TryTakeAsync(string bucket, double tokensPerSecond, int burst)
    {
        var reply = (long[]?)await db.ScriptEvaluateAsync(Script, [(RedisKey)$"ratelimit:merchant:{bucket}"], [tokensPerSecond, burst])
            ?? throw new InvalidOperationException("The rate limit script returned no result");

        return new RateLimitDecision(
            Allowed: reply[0] == 1,
            RetryAfter: TimeSpan.FromMilliseconds(reply[1]));
    }

    private const string Script = """
    -- KEYS[1] - bucket; ARGV[1] - tokens per second; ARGV[2] - burst (bucket capacity).
    -- Returns {allowed (1/0), milliseconds until a token is available}.
    local rate  = tonumber(ARGV[1])
    local burst = tonumber(ARGV[2])

    -- Redis time, not the callers' clocks: instances may disagree by milliseconds.
    local t   = redis.call('TIME')
    local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

    local state  = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
    local tokens = tonumber(state[1]) or burst      -- no bucket yet = a full one
    local ts     = tonumber(state[2]) or now

    -- Refill for the time since the last request, but never above the capacity.
    tokens = math.min(burst, tokens + math.max(0, now - ts) * rate / 1000)

    local allowed, retry_ms = 0, 0
    if tokens >= 1 then
      tokens  = tokens - 1
      allowed = 1
    else
      retry_ms = math.ceil((1 - tokens) * 1000 / rate)
    end

    redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', now)
    -- After this time the bucket is full again, so the key is not needed: a missing key means a full bucket.
    redis.call('PEXPIRE', KEYS[1], math.ceil(burst * 1000 / rate))
    return {allowed, retry_ms}
    """;
}

public record RateLimitDecision(bool Allowed, TimeSpan RetryAfter);
