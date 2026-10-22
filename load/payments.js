// k6 load test for Payments.Api.
// Run: k6 run -e RATE=300 -e DURATION=60s load/payments.js
import http from 'k6/http';
import { check } from 'k6';

const RATE = Number(__ENV.RATE || 200);
const DURATION = __ENV.DURATION || '60s';
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5100';
const MERCHANTS = 10000;

// Busy VUs = rate x response time (Little's law). A request is sent only if a VU is free at the moment it is due;
// otherwise k6 skips it and counts a dropped iteration. The pre-allocated pool rides out a 100 ms slowdown
// from the first second. The cap keeps k6 from eating the laptop's memory when the service is overloaded:
// the overload then shows up as dropped iterations, which the threshold below catches.
const MAX_VUS = 500;
const PRE_ALLOCATED_VUS = Math.min(MAX_VUS, Math.max(50, Math.ceil(RATE / 10)));

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
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<200', 'p(99)<500'],
    // Dropped iterations are load the service never received, and the slow answers they would have got
    // are missing from the percentiles. If more than 1% of the planned payments were dropped, the run does not count.
    dropped_iterations: [`rate<${RATE / 100}`],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

// A few large merchants get a big share of the payments, as in the bench/ experiments.
function pickMerchant() {
  return 1 + Math.floor(MERCHANTS * Math.pow(Math.random(), 4));
}

export default function () {
  const body = JSON.stringify({
    merchantId: pickMerchant(),
    amount: 1000 + Math.floor(Math.random() * 99001), // 10 to 1000 roubles, in kopecks
    currency: 'RUB',
  });

  const response = http.post(`${BASE_URL}/payments`, body, {
    headers: {
      'Content-Type': 'application/json',
      'Idempotency-Key': `${__VU}-${__ITER}-${Date.now()}`,
    },
  });

  check(response, { 'payment created': (r) => r.status === 201 });
}
