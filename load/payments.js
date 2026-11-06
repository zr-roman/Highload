// k6 load test for Payments.Api.
// Run: k6 run -e RATE=300 -e DURATION=60s load/payments.js
// With a hot merchant (stage 3): k6 run -e RATE=1000 -e HOT_SHARE=0.5 -e MERCHANTS=50 load/payments.js
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const RATE = Number(__ENV.RATE || 200);
const DURATION = __ENV.DURATION || '60s';
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5100';
const MERCHANTS = Number(__ENV.MERCHANTS || 10000);

// Stage 3: HOT_SHARE of the payments (0 to 1) go to HOT_MERCHANT, the rest to the other merchants equally.
// Without HOT_SHARE the mix of merchants is the stage 1 one, so those runs can be repeated.
const HOT_MERCHANT = Number(__ENV.HOT_MERCHANT || 1);
const HOT_SHARE = __ENV.HOT_SHARE ? Number(__ENV.HOT_SHARE) : null;

// A wrong value stops k6 before the run, not after a minute of load with a meaningless mix.
if (!Number.isInteger(MERCHANTS) || MERCHANTS < 2) {
  throw new Error(`MERCHANTS must be a whole number of at least 2, got ${__ENV.MERCHANTS}`);
}
if (!Number.isInteger(HOT_MERCHANT) || HOT_MERCHANT < 1 || HOT_MERCHANT > MERCHANTS) {
  throw new Error(`HOT_MERCHANT must be from 1 to MERCHANTS (${MERCHANTS}), got ${__ENV.HOT_MERCHANT}`);
}
if (HOT_SHARE !== null && !(HOT_SHARE >= 0 && HOT_SHARE <= 1)) {
  throw new Error(`HOT_SHARE must be from 0 to 1, got ${__ENV.HOT_SHARE}`);
}

// Busy VUs = rate x response time (Little's law). A request is sent only if a VU is free at the moment it is due;
// otherwise k6 skips it and counts a dropped iteration. A VU that k6 creates during the run comes too late
// for the spike that needed it, so the pool is created up front, big enough for answers of up to 250 ms:
// RATE / 4 VUs (the bank emulator alone answers in 50 +/- 20 ms). The cap keeps k6 from eating the laptop's
// memory when the service is overloaded: the overload then shows up as dropped iterations, which the threshold
// below catches.
const MAX_VUS = 500;
const PRE_ALLOCATED_VUS = Math.min(MAX_VUS, Math.max(50, Math.ceil(RATE / 4)));

// 429 from the merchant rate limit is an answer the service is meant to give, not a failure. By default k6 counts
// every status outside 200-399 as failed, and the limiter alone would fail the http_req_failed threshold.
// 201 carries the bank's decision, 202 means the bank has not answered yet.
http.setResponseCallback(http.expectedStatuses(201, 202, 429));

// Requests rejected by the rate limit, tagged like the requests themselves.
const rateLimited = new Counter('rate_limited');

const thresholds = {
  // Dropped iterations are load the service never received, and the slow answers they would have got
  // are missing from the percentiles. If more than 1% of the planned payments were dropped, the run does not count.
  // A dropped iteration never picked a merchant, so this threshold stays on all of them.
  dropped_iterations: [`rate<${RATE / 100}`],
};

if (HOT_SHARE === null) {
  thresholds.http_req_failed = ['rate<0.01'];
  thresholds.http_req_duration = ['p(95)<200', 'p(99)<500'];
} else {
  // The question of stage 3 is whether the hot merchant hurts the others, so the run is judged on them.
  thresholds['http_req_failed{merchant:other}'] = ['rate<0.01'];
  thresholds['http_req_duration{merchant:other}'] = ['p(95)<200', 'p(99)<500'];
  // 429 is not counted as failed, so a failed request of the hot merchant is a failure of the service.
  thresholds['http_req_failed{merchant:hot}'] = ['rate<0.01'];

  // The rest only shows numbers: k6 prints a tagged sub-metric in its summary only if it has a threshold,
  // and these always hold. The hot merchant's latency mixes fast 429s with real payments;
  // status:201 shows the payments alone.
  const show = (metrics, threshold) => metrics.forEach((metric) => { thresholds[metric] = [threshold]; });
  show(['http_req_duration{merchant:hot}', 'http_req_duration{merchant:hot,status:201}'], 'p(99)>=0');
  show(['http_reqs{merchant:hot}', 'http_reqs{merchant:other}'], 'count>=0');
  show(['rate_limited{merchant:hot}', 'rate_limited{merchant:other}'], 'count>=0');
}

export const options = {
  scenarios: {
    // Open model: requests arrive at a fixed rate no matter how fast the service answers,
    // so a slow service does not quietly reduce its own load (as a closed loop of virtual users would).
    payments: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: PRE_ALLOCATED_VUS,
      maxVUs: MAX_VUS,
    },
  },
  thresholds,
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

// Without HOT_SHARE a few large merchants get a big share of the payments, as in the bench/ experiments:
// merchant 1 about 10%, merchant 2 about 2%, then fewer and fewer.
// With HOT_SHARE the others are drawn from 1 to MERCHANTS - 1, and those at or above the hot merchant move up
// by one: that skips the hot merchant without a second draw.
function pickMerchant() {
  if (HOT_SHARE === null) {
    return 1 + Math.floor(MERCHANTS * Math.pow(Math.random(), 4));
  }
  if (Math.random() < HOT_SHARE) {
    return HOT_MERCHANT;
  }
  const other = 1 + Math.floor(Math.random() * (MERCHANTS - 1));
  return other >= HOT_MERCHANT ? other + 1 : other;
}

export default function () {
  const merchantId = pickMerchant();
  // Without HOT_SHARE merchant 1 is tagged hot as well: it is the largest merchant of the stage 1 mix.
  const tags = { merchant: merchantId === HOT_MERCHANT ? 'hot' : 'other' };

  const body = JSON.stringify({
    merchantId,
    amount: 1000 + Math.floor(Math.random() * 99001), // 10 to 1000 roubles, in kopecks
    currency: 'RUB',
  });

  const response = http.post(`${BASE_URL}/payments`, body, {
    headers: {
      'Content-Type': 'application/json',
      'Idempotency-Key': `${__VU}-${__ITER}-${Date.now()}`,
    },
    tags,
  });

  if (response.status === 429) {
    rateLimited.add(1, tags);
    return;
  }

  check(response, { 'payment created': (r) => r.status === 201 || r.status === 202 });
}
