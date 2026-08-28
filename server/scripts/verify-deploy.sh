#!/usr/bin/env bash
#
# Checks a deployed chessbin-api against the rules it is supposed to enforce.
#
# Run this against the real workers.dev URL after `npm run deploy`. The most valuable
# assertion is the loopback one: local development allows localhost origins via .dev.vars,
# and this proves that allowance did not follow the code into production.
#
#   npm run verify -- https://chessbin-api.you.workers.dev
#
set -uo pipefail

BASE="${1:-}"
if [[ -z "$BASE" ]]; then
  echo "usage: npm run verify -- https://chessbin-api.<subdomain>.workers.dev" >&2
  exit 2
fi
BASE="${BASE%/}"

failures=0

# check <description> <expected-status> <curl args...>
check() {
  local description="$1" expected="$2"; shift 2
  local actual
  actual=$(curl -sS -o /dev/null -w "%{http_code}" --max-time 15 "$@" 2>/dev/null)

  if [[ "$actual" == "$expected" ]]; then
    printf '  ok    %-46s %s\n' "$description" "$actual"
  else
    printf '  FAIL  %-46s got %s, wanted %s\n' "$description" "${actual:-no response}" "$expected"
    failures=$((failures + 1))
  fi
}

# header_check <description> <header> <expected-substring-or-EMPTY> <curl args...>
header_check() {
  local description="$1" header="$2" expected="$3"; shift 3
  local actual
  actual=$(curl -sS -D- -o /dev/null --max-time 15 "$@" 2>/dev/null \
    | grep -i "^${header}:" | tr -d '\r' | sed "s/^[^:]*: *//")

  if [[ "$expected" == "EMPTY" && -z "$actual" ]] \
    || [[ "$expected" != "EMPTY" && "$actual" == *"$expected"* ]]; then
    printf '  ok    %-46s %s\n' "$description" "${actual:-(absent)}"
  else
    printf '  FAIL  %-46s got "%s", wanted "%s"\n' "$description" "$actual" "$expected"
    failures=$((failures + 1))
  fi
}

echo "Verifying $BASE"
echo

# Reachability gate. Without this the suite reports a misleading near-pass against a host
# that never answered: checks asserting an *absent* header pass trivially when there is no
# response at all. Established the hard way against an undeployed Worker.
probe_body=$(curl -sS --max-time 15 "$BASE/health" 2>/dev/null)
probe=$(curl -sS -o /dev/null -w "%{http_code}" --max-time 15 "$BASE/health" 2>/dev/null)

not_deployed() {
  echo "  $1"
  echo
  echo "  Nothing of ours is answering at $BASE, so the checks below would all fail for the"
  echo "  same uninformative reason. Deploy first:"
  echo
  echo "      npx wrangler whoami     # \"not authenticated\" means start with: npx wrangler login"
  echo "      npm run deploy"
  exit 2
}

# No TLS at all. On a *.workers.dev host this means the subdomain has never been claimed —
# Cloudflare provisions the certificate as part of claiming it.
[[ "$probe" == "000" ]] && not_deployed "No response at all (TLS handshake failed)."

# Reachable, but Cloudflare's edge is answering rather than our code. Error 1042 is
# specifically "no Worker on this hostname": the subdomain exists, the Worker does not.
if [[ "$probe_body" == *"error code: 10"* ]]; then
  not_deployed "Cloudflare's edge answered ${probe} (${probe_body//$'\n'/ }) — the subdomain is live but no Worker is on it."
fi


echo "reachable and answering"
check "health, from the site"            200 -H "Origin: https://chessbin.com" "$BASE/health"
check "health, from www"                 200 -H "Origin: https://www.chessbin.com" "$BASE/health"
check "health, no Origin (referee path)" 200 "$BASE/health"
check "unknown route"                    404 -H "Origin: https://chessbin.com" "$BASE/nope"
check "preflight"                        204 -X OPTIONS -H "Origin: https://chessbin.com" "$BASE/vote/cast"

echo
echo "origins that must be refused"
check "another site"                     403 -H "Origin: https://evil.example" "$BASE/health"
check "lookalike hostname"               403 -H "Origin: https://chessbin.com.evil.example" "$BASE/health"
check "plain http"                       403 -H "Origin: http://chessbin.com" "$BASE/health"
check "localhost (dev-only allowance)"   403 -H "Origin: http://localhost:5000" "$BASE/health"

echo
echo "headers"
header_check "CORS echoes the site origin" "access-control-allow-origin" "https://chessbin.com" \
  -H "Origin: https://chessbin.com" "$BASE/health"
header_check "Vary, so caches stay honest" "vary" "Origin" \
  -H "Origin: https://chessbin.com" "$BASE/health"
header_check "no CORS header without an Origin" "access-control-allow-origin" "EMPTY" \
  "$BASE/health"

echo
if (( failures == 0 )); then
  echo "All checks passed."
else
  echo "$failures check(s) failed."
fi
exit $(( failures > 0 ? 1 : 0 ))
