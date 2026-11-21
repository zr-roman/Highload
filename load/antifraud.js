// k6 load test for the antifraud service alone, without Payments.Api: POST /v1/checks at a fixed rate.
// The criterion of stage 4: p99 of at most 20 ms at 1000 checks a second.
// Run: k6 run -e RATE=1000 -e DURATION=60s load/antifraud.js
// Steps, workers and Redis memory: load/antifraud-ladder.ps1.
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const RATE = Number(__ENV.RATE || 1000);
const DURATION = __ENV.DURATION || '60s';
const BASE_URL = __ENV.BASE_URL || 'http://127.0.0.1:5400';
// The velocity rule allows a standard merchant 100 checks a minute (ANTIFRAUD_VELOCITY_LIMIT). Uniform over 10 000
// merchants, 1500 checks a second are 9 a minute per merchant: every check is allowed, and the run measures the
// service, not the rule. DENY_SHARE sends that share of the checks to merchant 1 alone, which goes over its limit
// within the first seconds: the denied path, for comparison.
const MERCHANTS = Number(__ENV.MERCHANTS || 10000);
const DENY_SHARE = Number(__ENV.DENY_SHARE || 0);

if (!Number.isInteger(MERCHANTS) || MERCHANTS < 2) {
  throw new Error(`MERCHANTS must be a whole number of at least 2, got ${__ENV.MERCHANTS}`);
}
if (!(DENY_SHARE >= 0 && DENY_SHARE <= 1)) {
  throw new Error(`DENY_SHARE must be from 0 to 1, got ${__ENV.DENY_SHARE}`);
}

// A check answers in milliseconds: RATE / 20 VUs cover answers of up to 50 ms, the attempt timeout of Payments.Api.
// Slower answers are a failure of the criterion anyway; past the cap they show up as dropped iterations.
const MAX_VUS = 300;
const PRE_ALLOCATED_VUS = Math.min(MAX_VUS, Math.max(50, Math.ceil(RATE / 20)));

// 503 (Redis silent for longer than ANTIFRAUD_REDIS_TIMEOUT_MS) is a failure: Payments.Api would get "unknown".
http.setResponseCallback(http.expectedStatuses(200));

const decisions = new Counter('decisions');
// 503 by its reason: "overloaded" is the admission limit of the service, refused at once; "redis unavailable" is
// a Redis call that took longer than ANTIFRAUD_REDIS_TIMEOUT_MS.
const refusals = new Counter('refusals');

export const options = {
  scenarios: {
    // Open model, as in payments.js: a slow service does not lower its own load.
    checks: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: PRE_ALLOCATED_VUS,
      maxVUs: MAX_VUS,
    },
  },
  thresholds: {
    // The criterion of stage 4, and the 503s that Payments.Api would see as "unknown".
    http_req_duration: ['p(99)<20'],
    http_req_failed: ['rate<0.01'],
    // More than 1 % of the planned checks not sent: the run does not count.
    dropped_iterations: [`rate<${RATE / 100}`],
    // Shown in the summary only with a threshold; these always hold.
    'decisions{decision:allow}': ['count>=0'],
    'decisions{decision:deny}': ['count>=0'],
    'http_reqs{status:503}': ['count>=0'],
    'refusals{reason:overloaded}': ['count>=0'],
    'refusals{reason:redis}': ['count>=0'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

// A random version 4 UUID: every check is a new payment, as on the real path. No import from jslib.k6.io: the run
// must not need the network.
function uuid() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

function pickMerchant() {
  if (DENY_SHARE > 0 && Math.random() < DENY_SHARE) {
    return 1;
  }
  return 2 + Math.floor(Math.random() * (MERCHANTS - 1));
}

export default function () {
  const body = JSON.stringify({
    paymentId: uuid(),
    merchantId: pickMerchant(),
    amount: 1000 + Math.floor(Math.random() * 99001), // 10 to 1000 roubles, in kopecks
    currency: 'RUB',
    createdAt: new Date().toISOString(), // with Z: an offset, as the contract requires
  });

  const response = http.post(`${BASE_URL}/v1/checks`, body, { headers: { 'Content-Type': 'application/json' } });

  if (check(response, { 'decision returned': (r) => r.status === 200 })) {
    decisions.add(1, { decision: response.json('decision') });
  } else if (response.status === 503) {
    refusals.add(1, { reason: response.json('error') === 'overloaded' ? 'overloaded' : 'redis' });
  }
}
