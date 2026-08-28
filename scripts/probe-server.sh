#!/usr/bin/env bash
# Probes a running weft server for the guarantees it is supposed to hold.
#
# These are checked over HTTP on purpose. The client's happy path exercises none
# of them: every one describes what happens when something goes WRONG, and a
# client that behaves would never send these requests.
#
#   scripts/probe-server.sh <url> <join-secret> <token-a> <token-b>
#
# The server must be running with Weft__MinClient set above 0.0.1, or the version
# check reports a failure that is really a missing setting: with no floor the
# write is allowed through and refused later for its body, as a 400.
set -uo pipefail

URL=${1:?usage: probe-server.sh <url> <join-secret> <token-a> <token-b>}
JOIN=${2:?}
TA=${3:?}
TB=${4:?}

pass=0; fail=0
check() { # name expected actual
  if [ "$2" = "$3" ]; then printf '  \033[32mok\033[0m   %-52s %s\n' "$1" "$3"; pass=$((pass+1))
  else printf '  \033[31mFAIL\033[0m %-52s got %s, wanted %s\n' "$1" "$3" "$2"; fail=$((fail+1)); fi
}
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

echo "Authentication"
check "no token is refused"           401 "$(code "$URL/v1/heads")"
check "a made-up token is refused"    401 "$(code -H 'Authorization: Bearer wsk_nope' "$URL/v1/heads")"
check "a real token is accepted"      200 "$(code -H "Authorization: Bearer $TA" "$URL/v1/heads")"

echo "Enrolment"
check "wrong join secret is refused"  403 "$(code -X POST "$URL/v1/enrol" -H 'Content-Type: application/json' \
  -d '{"joinSecret":"wrong","machineId":"x","machineName":"x","platform":"x","workspace":"deadbeefdead"}')"
# The body goes through a variable, never inline in a command substitution.
# bash 3.2, which macOS still ships, mis-parses escaped double quotes nested that
# deep: the braces end up unquoted, BRACE EXPANSION fires, and one request
# becomes five, each carrying a fragment of the JSON. The failure looks like a
# server bug and is not one.
body="{\"joinSecret\":\"$JOIN\",\"machineId\":\"probe\",\"machineName\":\"probe\",\"platform\":\"probe\",\"workspace\":\"000000000000\"}"
check "a different workspace key is refused" 409 "$(code -X POST "$URL/v1/enrol" -H 'Content-Type: application/json' -d "$body")"

# The bug the mangled request above found the first time it ran.
check "a malformed body is a 400, not a 500" 400 "$(code -X POST "$URL/v1/enrol" -H 'Content-Type: application/json' -d '"not":"an object"')"
check "an empty body is a 400"               400 "$(code -X POST "$URL/v1/enrol" -H 'Content-Type: application/json' -d '')"
check "truncated JSON is a 400"              400 "$(code -X POST "$URL/v1/enrol" -H 'Content-Type: application/json' -d '{"joinSecret":')"

echo "Client version"
# Writes are refused to an old build; reads are NOT. Blocking reads would strand
# an outdated machine with no way to fetch what it needs, including its own work.
check "a write with no version is refused"   426 "$(code -X PUT "$URL/v1/head" -H "Authorization: Bearer $TA" \
  -H 'Content-Type: application/json' -d '{"snapshot":"00"}')"
check "a write from an old build is refused" 426 "$(code -X PUT "$URL/v1/head" -H "Authorization: Bearer $TA" \
  -H 'Weft-Client: 0.0.1' -H 'Content-Type: application/json' -d '{"snapshot":"00"}')"
check "a READ from that same build works"    200 "$(code -H "Authorization: Bearer $TA" -H 'Weft-Client: 0.0.1' "$URL/v1/heads")"

echo "Namespace isolation"
before=$(curl -s "$URL/v1/heads" -H "Authorization: Bearer $TA" \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["heads"][0]["snapshot"])')
curl -s -o /dev/null -X PUT "$URL/v1/head" -H "Authorization: Bearer $TB" -H 'Weft-Client: 99.0.0' \
  -H 'Content-Type: application/json' -d '{"snapshot":"'"$before"'"}'
after=$(curl -s "$URL/v1/heads" -H "Authorization: Bearer $TA" \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["heads"][0]["snapshot"])')
check "one machine cannot move another's pointer" "$before" "$after"

echo "Revocation"
# Nothing here actually revokes. A probe that withdrew a real token would break
# the setup it was handed, and the three properties worth checking are all
# reachable without touching a live machine.
#
# The unknown id is the important one. Answered as 404, this endpoint tells
# anyone who asks which machine ids exist; the secret has to be judged first, so
# a wrong secret gets 403 whether or not the machine is real.
check "a wrong join secret cannot revoke"   403 "$(code -X POST "$URL/v1/machines/probe-absent/revoke" \
  -H 'Content-Type: application/json' -d '{"joinSecret":"wrong"}')"
check "a wrong secret hides whether an id exists" 403 "$(code -X POST "$URL/v1/machines/definitely-not-here/revoke" \
  -H 'Content-Type: application/json' -d '{"joinSecret":"wrong"}')"
# A bearer token is what a lost machine has. If it bought revocation, whoever
# took that machine could lock out everyone else first.
check "a bearer token does not buy revocation" 403 "$(code -X POST "$URL/v1/machines/probe-absent/revoke" \
  -H "Authorization: Bearer $TA" -H 'Weft-Client: 99.0.0' \
  -H 'Content-Type: application/json' -d '{"joinSecret":""}')"
revoke_body="{\"joinSecret\":\"$JOIN\"}"
check "the right secret on an unknown id is a 404" 404 "$(code -X POST "$URL/v1/machines/definitely-not-here/revoke" \
  -H 'Content-Type: application/json' -d "$revoke_body")"

echo "Object immutability"
obj=$(curl -s "$URL/v1/heads" -H "Authorization: Bearer $TA" \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["heads"][0]["snapshot"])')
curl -s -o /dev/null -X PUT "$URL/v1/objects/$obj" -H "Authorization: Bearer $TB" -H 'Weft-Client: 99.0.0' \
  --data-binary 'OVERWRITTEN'
check "an existing object cannot be replaced" 0 \
  "$(curl -s "$URL/v1/objects/$obj" -H "Authorization: Bearer $TA" | grep -c 'OVERWRITTEN')"

echo
if [ "$fail" -eq 0 ]; then printf '\033[32m%d checks passed\033[0m\n' "$pass"; exit 0
else printf '\033[31m%d passed, %d FAILED\033[0m\n' "$pass" "$fail"; exit 1; fi
