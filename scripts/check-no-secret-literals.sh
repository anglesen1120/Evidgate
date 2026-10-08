#!/usr/bin/env bash
# check-no-secret-literals.sh — AC3 (10.1.1-T2.1): no literal secret may be
# committed to the repository or baked into an image.
#
# Scope: every git-tracked and untracked-not-ignored file under src/, tests/,
# packages/, apps/, scripts/, infra/, .github/ and .husky/, plus every
# Dockerfile* / *.Dockerfile / compose*.yml / docker-compose*.yml anywhere,
# appsettings*.json, .env* templates, .npmrc, and the *.yml / *.yaml / *.json /
# *.props / *.config files at the repository root. Build output
# (node_modules/, bin/, obj/, artifacts/) is skipped only when it is untracked;
# a tracked file is always scanned. Every file is read as bytes (grep -a,
# LC_ALL=C): no file is skipped as binary, and a grep error fails the scan.
#
# Rules — a hit prints `file:line: <rule id>` and never the matched value:
#   pem-private-key      a PEM private-key header
#   aws-access-key-id    an AWS access key id
#   github-token         a GitHub token (gh*_… / github_pat_…)
#   slack-token          a Slack token (xox[baprs]-…)
#   jwt                  a JWT-shaped string (eyJ….eyJ….)
#   connstr-password     a connection string carrying a literal password value
#   url-userinfo         a URL with a literal user:password userinfo
#   secret-assignment    a configuration or shell key that names a secret
#                        (password, passwd, secret, token, api/access/secret/
#                        signing/private key, pepper, connection string)
#                        assigned a literal value — `key: v`, `key=v`,
#                        `export key=v`, `env key=v`, `-e key=v`, Dockerfile
#                        `ENV key v`, and JSON keys anywhere on a line
#   secret-key-material  any in-scope file (C#/TypeScript included): an
#                        identifier naming a secret (Pepper, SigningKey,
#                        Secret, Password, Token, …) assigned a quoted
#                        base64-looking literal of 32 characters or more
#
# Placeholders pass: empty, $NAME, ${NAME}, ${NAME:?message}, ${NAME:-} (empty
# default), $(...), ${{ ... }}, %NAME%, __PLACEHOLDER__, <...>, {interpolation},
# ***, …, null/true/false, and structured (object/array) values. A
# `${NAME:-default}` / `${NAME-default}` with a non-empty default is a literal.
#
# Reviewed exceptions live in scripts/no-secret-literals-allow.txt:
#   <path><TAB><rule id><TAB><reason>[<TAB><line regex>]
# <path> is an exact repository path or a glob whose `*` / `?` never cross `/`
# (only `**` does). <rule id> is one exact rule. The optional <line regex> is a
# POSIX extended regex the hit line must match for the exception to apply, so
# an exception covers the reviewed values only, not every later literal in the
# same file.
#
# Usage:
#   scripts/check-no-secret-literals.sh [--root <dir>]
#   scripts/check-no-secret-literals.sh --self-test
# Exit: 0 clean, 1 any hit, 2 usage, configuration or read error.
set -euo pipefail
# Byte semantics everywhere: bracket ranges, case patterns and sort never
# depend on the caller's locale.
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ALLOW_FILE="$SCRIPT_DIR/no-secret-literals-allow.txt"
SEP=$'\001'

RULE_IDS="pem-private-key aws-access-key-id github-token slack-token jwt connstr-password url-userinfo secret-assignment secret-key-material"

usage() {
  cat <<'EOF'
usage: scripts/check-no-secret-literals.sh [--root <dir>] | --self-test

  --root <dir>   scan <dir> instead of the repository root
  --self-test    build a throwaway tree, prove a clean tree passes, every rule
                 rejects a runtime-built dummy without printing it, and an
                 allowlist entry covers only its reviewed lines
EOF
}

die() {
  echo "check-no-secret-literals: $*" >&2
  exit 2
}

# --- file discovery and scoping ----------------------------------------------

# list_files <root> — NUL-separated records `t<path>` (tracked) or `u<path>`
# (untracked, not ignored).
list_files() {
  local root="$1" p
  if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    git -C "$root" ls-files -z --cached | while IFS= read -r -d '' p; do printf 't%s\0' "$p"; done
    git -C "$root" ls-files -z --others --exclude-standard | while IFS= read -r -d '' p; do printf 'u%s\0' "$p"; done
  else
    (cd "$root" && find . -type f -print0) | while IFS= read -r -d '' p; do printf 'u%s\0' "${p#./}"; done
  fi
}

# in_scope <path> <t|u>
in_scope() {
  local p="$1" b="${1##*/}"
  case "$p" in .git/*|*/.git/*) return 1 ;; esac
  if [ "$2" = u ]; then
    case "/$p" in
      */node_modules/*|*/bin/*|*/obj/*|*/artifacts/*) return 1 ;;
    esac
  fi
  case "$p" in
    src/*|tests/*|packages/*|apps/*|scripts/*|infra/*|.github/*|.husky/*) return 0 ;;
  esac
  case "$b" in
    *.dockerignore) return 1 ;;
    Dockerfile*|*.Dockerfile|compose*.yml|compose*.yaml|docker-compose*.yml|docker-compose*.yaml) return 0 ;;
    appsettings*.json|.env*|.npmrc) return 0 ;;
  esac
  case "$p" in
    */*) return 1 ;;
  esac
  case "$b" in
    *.yml|*.yaml|*.json|*.props|*.config) return 0 ;;
  esac
  return 1
}

# The secret-assignment rule reads configuration and shell files only: a
# `key: value` / `key=value` pair in those formats is a declaration, whereas the
# same text in C#/TypeScript is a code expression that the shape,
# connection-string and key-material rules cover.
is_config_file() {
  local p="$1" b="${1##*/}"
  case "$p" in .husky/*) return 0 ;; esac
  case "$b" in
    *.dockerignore) return 1 ;;
    Dockerfile*|*.Dockerfile|compose*.yml|compose*.yaml|docker-compose*.yml|docker-compose*.yaml) return 0 ;;
    appsettings*.json|.env*|.npmrc) return 0 ;;
    *.json|*.yml|*.yaml|*.props|*.config|*.toml|*.ini|*.properties|*.sh) return 0 ;;
  esac
  return 1
}

# --- value classification ----------------------------------------------------

PH_NAME='[A-Za-z_][A-Za-z0-9_]*'

# A value is a placeholder (not a literal secret) when it is empty, an
# environment/secret reference without a literal default, a documented
# placeholder, a mask or a structured value. Numbers are literals.
is_placeholder() {
  local v="$1"
  v="${v#"${v%%[![:space:]]*}"}"
  v="${v%"${v##*[![:space:]]}"}"
  v="${v%,}"
  v="${v%"${v##*[![:space:]]}"}"
  case "$v" in \"*) v="${v#\"}" ;; \'*) v="${v#\'}" ;; esac
  case "$v" in *\") v="${v%\"}" ;; *\') v="${v%\'}" ;; esac
  case "$v" in
    ""|"null"|"true"|"false"|"***"|"…"|"..."|"[]"|"{}"|'"'|"'") return 0 ;;
    '__'*'__') return 0 ;;
    '<'*'>') return 0 ;;
    '%'*'%') return 0 ;;
    '${{'*'}}') return 0 ;;
  esac
  [[ "$v" =~ ^\$(${PH_NAME}|[0-9]+|[@*#?])$ ]] && return 0
  [[ "$v" =~ ^\$\{${PH_NAME}\}$ ]] && return 0
  [[ "$v" =~ ^\$\{${PH_NAME}:?\?[^}]*\}$ ]] && return 0
  [[ "$v" =~ ^\$\{${PH_NAME}:?-\}$ ]] && return 0
  [[ "$v" =~ ^\$\(.*\)$ ]] && return 0
  case "$v" in
    '$'*) return 1 ;;
    '{'*'}') return 0 ;;
  esac
  return 1
}

# --- grep driver -------------------------------------------------------------

# grep_lines <list-file> <rule> <grep option>... — print `file<SEP>line:content`
# for every matching line of the NUL-separated file list. Files are fed through
# xargs (no ARG_MAX limit), read as bytes, never skipped as binary; a grep
# error (exit > 1) in any batch fails the scan with exit 2.
grep_lines() {
  local list="$1" rule="$2"
  shift 2
  [ -s "$list" ] || return 0
  LC_ALL=C xargs -0 sh -c 'LC_ALL=C grep -a -n -H --null "$@"; rc=$?; [ "$rc" -le 1 ] || exit 2' grep-batch "$@" -- <"$list" |
    LC_ALL=C tr '\000' '\001' ||
    die "grep failed while applying rule '$rule' (unreadable file or grep error); the scan cannot pass"
}

# The scan reads every in-scope file once, selecting the candidate lines: lines
# naming a secret word (the only lines connstr-password, secret-key-material
# and secret-assignment can hit) and lines holding the fixed prefix of a shape
# rule. Each rule then applies its full pattern to the candidates only.
KEYWORD_RE='passw|pwd|secret|token|pepper|signing|api[_-]?key|private[_-]?key|access[_-]?key|connection[_-]?string'
SHAPE_HINT_RE='-----BEGIN|AKIA|gh[pousr]_|github_pat_|xox[baprs]-|eyJ|://'

SHAPE_RULES=(pem-private-key aws-access-key-id github-token slack-token jwt url-userinfo)
SHAPE_RES=(
  '-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----'
  'AKIA[0-9A-Z]{16}'
  'gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{20,}'
  'xox[baprs]-[A-Za-z0-9-]{10,}'
  'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.'
  '[a-zA-Z][a-zA-Z0-9+.-]*://[^/@:[:space:]]+:[^/@[:space:]]+@'
)

# refine_lines <records-file> <rule> <grep option>... — print the records
# (`file<SEP>line:content`) whose content matches. The pattern sees the line
# content alone, so `^` anchors keep their meaning. A grep error fails the scan.
refine_lines() {
  local recs="$1" rule="$2"
  shift 2
  [ -s "$recs" ] || return 0
  LC_ALL=C awk -v sep="$SEP" '{ r = substr($0, index($0, sep) + 1); print substr(r, index(r, ":") + 1) }' "$recs" >"$recs.content" ||
    die "could not split records for rule '$rule'"
  {
    LC_ALL=C grep -a -n "$@" -- "$recs.content"
    rc=$?
    [ "$rc" -le 1 ] || exit 2
  } | LC_ALL=C cut -d: -f1 >"$recs.idx" ||
    die "grep failed while applying rule '$rule' (grep error); the scan cannot pass"
  LC_ALL=C awk 'NR == FNR { want[$1] = 1; next } want[FNR]' "$recs.idx" "$recs" ||
    die "could not select records for rule '$rule'"
}

# emit <file> <line> <rule> <content> — one internal hit record.
emit() {
  printf '%s%s%s%s%s%s%s\n' "$1" "$SEP" "$2" "$SEP" "$3" "$SEP" "$4"
}

# Shape rules match anywhere in a line; the matched text is never printed.
shape_rules() {
  local recs="$1" out l file rest n content i
  local -a opts=()
  for i in "${!SHAPE_RES[@]}"; do opts+=(-e "${SHAPE_RES[$i]}"); done
  out="$(refine_lines "$recs" shapes -E "${opts[@]}")" || exit 2
  [ -n "$out" ] || return 0
  while IFS= read -r l; do
    file="${l%%"$SEP"*}"
    rest="${l#*"$SEP"}"
    n="${rest%%:*}"
    content="${rest#*:}"
    for i in "${!SHAPE_RES[@]}"; do
      [[ "$content" =~ ${SHAPE_RES[$i]} ]] && emit "$file" "$n" "${SHAPE_RULES[$i]}" "$content"
    done
  done <<<"$out"
  return 0
}

# for_each_match <regex> <content> — sets MATCHES to the last capture group
# (the value) of every non-overlapping match of <regex> in <content>,
# separated by SEP (case-insensitive).
for_each_match() {
  local re="$1" s="$2" m
  MATCHES=""
  shopt -s nocasematch
  while [ -n "$s" ] && [[ "$s" =~ $re ]]; do
    MATCHES+="${BASH_REMATCH[$((${#BASH_REMATCH[@]} - 1))]}$SEP"
    m="${BASH_REMATCH[0]}"
    [ -n "$m" ] || break
    s="${s#*"$m"}"
  done
  shopt -u nocasematch
}

# A value as written after a key: a quoted string, a shell expansion, an
# {interpolation}, or a bare token. A leading `=` (as in `==`) is not a value.
VALUE_RE='("[^"]*"|'"'"'[^'"'"']*'"'"'|\$\{\{[^}]*\}\}|\$\{[^}]*\}|\$\([^)]*\)|\{[^}]*\}|[^=[:space:],;}#"'"'"'][^[:space:],;}#]*)?'

# A connection string carries a literal password when the value after the
# password key is not a placeholder. The key must sit in connection-string
# context: the password key directly followed by an equals sign right after
# the start, `;`, a quote or whitespace, or with whitespace around the equals
# sign right after `;` or a quote — so a C# object-initializer member and an
# upper-case shell/config variable named after a password (left to
# secret-assignment) do not match.
CONNSTR_RE="((^|[;\"'[:space:]])(password|pwd)=|(;[[:space:]]*|[\"'])(password|pwd)[[:space:]]*=[[:space:]]*)$VALUE_RE"

connstr_rule() {
  local recs="$1" out l file rest n content v
  out="$(refine_lines "$recs" connstr-password -E -i -e "$CONNSTR_RE")" || exit 2
  [ -n "$out" ] || return 0
  while IFS= read -r l; do
    file="${l%%"$SEP"*}"
    rest="${l#*"$SEP"}"
    n="${rest%%:*}"
    content="${rest#*:}"
    for_each_match "$CONNSTR_RE" "$content"
    while [ -n "$MATCHES" ]; do
      v="${MATCHES%%"$SEP"*}"
      MATCHES="${MATCHES#*"$SEP"}"
      case "$v" in '"'*'"') v="${v#\"}"; v="${v%\"}" ;; esac
      # A quoted value that is itself a connection-string fragment ends at `;`.
      v="${v%%;*}"
      if ! is_placeholder "$v"; then
        emit "$file" "$n" connstr-password "$content"
        break
      fi
    done
  done <<<"$out"
}

SECRET_WORD='(pass(word|wd)|secret([_-]?key)?|token|api[_-]?key|access[_-]?key|signing[_-]?key|private[_-]?key|connection[_-]?string|pepper)'

# A configuration/shell key that names a secret, assigned a scalar literal.
# The key must *end* with the secret word (so `Microsoft.IdentityModel.Tokens`
# and `UserSecrets` do not match) and may sit anywhere on the line (after
# `export `, `env `, `-e `, `{`, `,`) except inside a `${NAME…}` expansion; the
# value must not be a placeholder. A value that is itself a connection string
# or key=value fragment (an `=` that is not base64 padding) is left to
# connstr-password, which inspects the password inside it.
ASSIGN_RE="(^|[^A-Za-z0-9_.{\$-]|(^|[^\$])\{)[\"']?[A-Za-z0-9_.-]*$SECRET_WORD[\"']?[[:space:]]*[:=][[:space:]]*$VALUE_RE"
ENV_SPACE_RE="^[[:space:]]*ENV[[:space:]]+[A-Za-z0-9_.-]*$SECRET_WORD[[:space:]]+$VALUE_RE"

assignment_rule() {
  local recs="$1" out l file rest n content v hit
  out="$(refine_lines "$recs" secret-assignment -E -i -e "$ASSIGN_RE" -e "$ENV_SPACE_RE")" || exit 2
  [ -n "$out" ] || return 0
  while IFS= read -r l; do
    file="${l%%"$SEP"*}"
    rest="${l#*"$SEP"}"
    n="${rest%%:*}"
    content="${rest#*:}"
    hit=0
    # VALUE_RE is the last group of each pattern.
    for_each_match "$ASSIGN_RE" "$content"
    local all="$MATCHES"
    for_each_match "$ENV_SPACE_RE" "$content"
    all+="$MATCHES"
    while [ -n "$all" ]; do
      v="${all%%"$SEP"*}"
      all="${all#*"$SEP"}"
      case "$v" in '{'*|'['*) continue ;; esac
      case "$v" in *'='[!=]*) continue ;; esac
      if ! is_placeholder "$v"; then
        hit=1
        break
      fi
    done
    [ "$hit" -eq 0 ] || emit "$file" "$n" secret-assignment "$content"
  done <<<"$out"
}

KEYMAT_WORD='(pepper|signing[_-]?key|secret|passw(or)?d|pwd|token|api[_-]?key|private[_-]?key|access[_-]?key)'
KEYMAT_RE="[A-Za-z0-9_]*$KEYMAT_WORD[A-Za-z0-9_]*[\"']?[[:space:]]*(=>|:|=|,)[[:space:]]*[@\$]?[\"'\`]([A-Za-z0-9+/_-]{32,}={0,2})[\"'\`]"

# A secret-named identifier assigned a base64-looking literal: 32+ characters
# from the base64 alphabet holding upper case, lower case and a digit or +/ —
# a hex digest or an UPPER_CASE / PascalCase name does not qualify.
keymat_rule() {
  local recs="$1" out l file rest n content v
  out="$(refine_lines "$recs" secret-key-material -E -i -e "$KEYMAT_RE")" || exit 2
  [ -n "$out" ] || return 0
  while IFS= read -r l; do
    file="${l%%"$SEP"*}"
    rest="${l#*"$SEP"}"
    n="${rest%%:*}"
    content="${rest#*:}"
    for_each_match "$KEYMAT_RE" "$content"
    while [ -n "$MATCHES" ]; do
      v="${MATCHES%%"$SEP"*}"
      MATCHES="${MATCHES#*"$SEP"}"
      case "$v" in *[[:upper:]]*) ;; *) continue ;; esac
      case "$v" in *[[:lower:]]*) ;; *) continue ;; esac
      case "$v" in *[[:digit:]+/]*) ;; *) continue ;; esac
      emit "$file" "$n" secret-key-material "$content"
      break
    done
  done <<<"$out"
}

# --- allowlist ---------------------------------------------------------------

ALLOW_PATH_RE=()
ALLOW_RULE=()
ALLOW_LINE_RE=()

# glob_to_regex <glob> — `**` crosses `/`, `*` and `?` do not.
glob_to_regex() {
  local g="$1" out="" c i=0 n=${#1}
  while [ "$i" -lt "$n" ]; do
    c="${g:$i:1}"
    if [ "$c" = '*' ] && [ "${g:$((i + 1)):1}" = '*' ]; then
      out+='.*'
      i=$((i + 2))
      continue
    fi
    case "$c" in
      '*') out+='[^/]*' ;;
      '?') out+='[^/]' ;;
      [.\[\]\(\)\{\}\^\$\|\+\\]) out+="\\$c" ;;
      *) out+="$c" ;;
    esac
    i=$((i + 1))
  done
  printf '^%s$' "$out"
}

# Allowlist lines are `path<TAB>rule<TAB>reason[<TAB>line-regex]`; `#` starts
# a comment. A malformed entry, an unknown or wildcard rule, or an invalid line
# regex is a configuration error.
load_allowlist() {
  ALLOW_PATH_RE=()
  ALLOW_RULE=()
  ALLOW_LINE_RE=()
  [ -f "$ALLOW_FILE" ] || return 0
  local path rule reason linere extra rc
  while IFS=$'\t' read -r path rule reason linere extra || [ -n "$path" ]; do
    case "$path" in ''|\#*) continue ;; esac
    if [ -z "$rule" ] || [ -z "$reason" ] || [ -n "${extra:-}" ]; then
      echo "check-no-secret-literals: allowlist entry needs path<TAB>rule<TAB>reason[<TAB>line regex]: $path" >&2
      return 2
    fi
    case " $RULE_IDS " in
      *" $rule "*) ;;
      *)
        echo "check-no-secret-literals: allowlist entry names unknown rule '$rule' (one exact rule id per entry): $path" >&2
        return 2
        ;;
    esac
    if [ -n "${linere:-}" ]; then
      rc=0
      [[ "" =~ $linere ]] || rc=$?
      if [ "$rc" -eq 2 ]; then
        echo "check-no-secret-literals: allowlist entry has an invalid line regex: $path" >&2
        return 2
      fi
    fi
    ALLOW_PATH_RE+=("$(glob_to_regex "$path")")
    ALLOW_RULE+=("$rule")
    ALLOW_LINE_RE+=("${linere:-}")
  done <"$ALLOW_FILE"
  return 0
}

allowlisted() {
  local file="$1" rule="$2" content="$3" i=0 n=${#ALLOW_RULE[@]}
  while [ "$i" -lt "$n" ]; do
    if [ "${ALLOW_RULE[$i]}" = "$rule" ] && [[ "$file" =~ ${ALLOW_PATH_RE[$i]} ]]; then
      if [ -z "${ALLOW_LINE_RE[$i]}" ] || [[ "$content" =~ ${ALLOW_LINE_RE[$i]} ]]; then
        return 0
      fi
    fi
    i=$((i + 1))
  done
  return 1
}

# --- scan --------------------------------------------------------------------

# collect_hits <root> <tmpdir> — print every internal hit record, unfiltered.
collect_hits() {
  local root="$1" tmp="$2" rec kind p
  local all="$tmp/files" cfg="$tmp/cfg"
  : >"$all"
  : >"$cfg"
  cd "$root"
  while IFS= read -r -d '' rec; do
    kind="${rec:0:1}"
    p="${rec:1}"
    [ -n "$p" ] || continue
    # A tracked file deleted in the work tree, a dangling link or a directory
    # (submodule) has no content to read.
    [ -f "$p" ] || continue
    in_scope "$p" "$kind" || continue
    printf '%s\0' "$p" >>"$all"
    if is_config_file "$p"; then printf '%s\0' "$p" >>"$cfg"; fi
  done < <(list_files "$root")

  grep_lines "$all" candidates -E -i -e "$KEYWORD_RE" -e "$SHAPE_HINT_RE" >"$tmp/cand" || exit 2
  shape_rules "$tmp/cand"
  refine_lines "$tmp/cand" secret-word -E -i -e "$KEYWORD_RE" >"$tmp/kw" || exit 2
  grep_lines "$cfg" secret-word -E -i -e "$KEYWORD_RE" >"$tmp/kwcfg" || exit 2
  connstr_rule "$tmp/kw"
  keymat_rule "$tmp/kw"
  assignment_rule "$tmp/kwcfg"
  return 0
}

# run_scan <root> — print the non-allowlisted hits as `file:line: rule`;
# 0 clean, 1 any hit, 2 read error.
run_scan() {
  local root="$1" tmp hits rc=0 line file rest n rule content filtered=""
  tmp="$(mktemp -d)"
  hits="$(collect_hits "$root" "$tmp")" || rc=$?
  rm -rf "$tmp"
  [ "$rc" -eq 0 ] || return 2
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    file="${line%%"$SEP"*}"
    rest="${line#*"$SEP"}"
    n="${rest%%"$SEP"*}"
    rest="${rest#*"$SEP"}"
    rule="${rest%%"$SEP"*}"
    content="${rest#*"$SEP"}"
    allowlisted "$file" "$rule" "$content" && continue
    filtered+="$file:$n: $rule"$'\n'
  done <<<"$hits"
  if [ -n "$filtered" ]; then
    printf '%s' "$filtered" | LC_ALL=C sort -u
    return 1
  fi
  return 0
}

# --- self-test ---------------------------------------------------------------

SELF_TEST_TMP=""
cleanup_self_test() {
  [ -n "$SELF_TEST_TMP" ] && rm -rf "$SELF_TEST_TMP"
  return 0
}

# scan_tree <root> — run_scan output; sets SCAN_RC.
scan_tree() {
  SCAN_RC=0
  SCAN_OUT="$(run_scan "$1")" || SCAN_RC=$?
}

# expect_clean <root> <what>
expect_clean() {
  scan_tree "$1"
  if [ "$SCAN_RC" -ne 0 ]; then
    echo "self-test: FAILED — $2 reported a hit (exit $SCAN_RC)" >&2
    printf '%s\n' "$SCAN_OUT" >&2
    return 1
  fi
  echo "self-test: $2 passed"
}

# expect_hit <root> <what> <rule> <dummy> — the scan must reject with <rule>
# and must not print the dummy value.
expect_hit() {
  local root="$1" what="$2" rule="$3" dummy="$4"
  scan_tree "$root"
  if [ "$SCAN_RC" -ne 1 ]; then
    echo "self-test: FAILED — $what was not rejected (exit $SCAN_RC)" >&2
    return 1
  fi
  case "$SCAN_OUT" in
    *": $rule"*) ;;
    *)
      echo "self-test: FAILED — $what was not reported as $rule" >&2
      printf '%s\n' "$SCAN_OUT" >&2
      return 1
      ;;
  esac
  case "$SCAN_OUT" in
    *"$dummy"*)
      echo "self-test: FAILED — $what leaked the matched value" >&2
      return 1
      ;;
  esac
  echo "self-test: $what rejected ($rule)"
}

# with_file <root> <relpath> <content> <what> <rule> <dummy> — write one file
# into an otherwise clean tree, expect the rule, then remove the file.
reject_case() {
  local root="$1" rel="$2" body="$3" what="$4" rule="$5" dummy="$6"
  mkdir -p "$root/$(dirname "$rel")"
  printf '%s\n' "$body" >"$root/$rel"
  expect_hit "$root" "$what" "$rule" "$dummy" || return 1
  rm -f "$root/$rel"
}

self_test() {
  SELF_TEST_TMP="$(mktemp -d)"
  trap cleanup_self_test EXIT
  local tmp="$SELF_TEST_TMP/tree"
  mkdir -p "$tmp/infra" "$tmp/src"
  # The real allowlist must not mask a self-test case.
  ALLOW_FILE="$SELF_TEST_TMP/allow.txt"
  : >"$ALLOW_FILE"
  load_allowlist || return 1

  # Every dummy is built at runtime so this script never contains the literal
  # shape it looks for.
  local pw b64 pem aws ghp slack jwt
  pw="$(printf '%s%s' 'not-a-real' 'Secret123')"
  b64="$(printf '%s%s' 'Zm9vYmFyQmF6UXV4' 'MTIzNDU2Nzg5MGFiY2RlZg==')"
  pem="$(printf '%s %s' '-----BEGIN RSA' 'PRIVATE KEY-----')"
  aws="$(printf '%s%s' 'AK''IA' 'ABCDEFGHIJKLMNOP')"
  ghp="$(printf '%s%s' 'gh''p_' 'abcdefghijklmnopqrstuvwxyz0123456789AB')"
  slack="$(printf '%s%s' 'xox''b-' '0123456789-abcdefghij')"
  jwt="$(printf '%s.%s.%s' 'ey''JhbGciOiJIUzI1NiJ9' 'ey''JzdWIiOiIxMjM0NTY3ODkwIn0' 'c2lnbmF0dXJl')"

  cat >"$tmp/config.json" <<'EOF'
{
  "name": "clean",
  "port": 8080,
  "password": "__PLACEHOLDER__",
  "connectionString": "${EVIDGATE__DATABASE__CONNECTIONSTRING}"
}
EOF
  cat >"$tmp/Dockerfile" <<'EOF'
FROM scratch
ENV APP_MODE=production
ARG DB_PASSWORD
EOF
  cat >"$tmp/infra/compose.dev.yml" <<'EOF'
services:
  app:
    environment:
      APP_MODE: production
      DB_PASSWORD: ${EVIDGATE_DB_PASSWORD:?set EVIDGATE_DB_PASSWORD}
      API_TOKEN: ${EVIDGATE_API_TOKEN}
      PG_PASSWORD: $PG_PASSWORD
EOF
  cat >"$tmp/src/Options.cs" <<'EOF'
public sealed class CiAuthOptions
{
    public string? Pepper { get; internal set; }
    public const string SigningKeyName = "EVIDGATE__CI__SIGNINGKEY";
}
EOF
  expect_clean "$tmp" "clean tree" || return 1

  # One rejecting case per rule.
  reject_case "$tmp" "config.json" "$(printf '{\n  "password": "%s"\n}' "$pw")" \
    "JSON config literal" secret-assignment "$pw" || return 1
  reject_case "$tmp" "src/min.json" "$(printf '{"name":"x","apiKey":"%s"}' "$pw")" \
    "mid-line JSON key" secret-assignment "$pw" || return 1
  reject_case "$tmp" "Dockerfile.ci" "$(printf 'FROM scratch\nENV DB_PASSWORD %s' "$pw")" \
    "Dockerfile ENV space form" secret-assignment "$pw" || return 1
  reject_case "$tmp" "scripts/run.sh" "$(printf 'export DB_PASSWORD=%s\ndocker run -e API_TOKEN=%s img' "$pw" "$pw")" \
    "shell export / -e literal" secret-assignment "$pw" || return 1
  reject_case "$tmp" "infra/compose.prod.yml" "$(printf 'services:\n  db:\n    environment:\n      POSTGRES_PASSWORD: ${PGPW:-%s}' "$pw")" \
    "placeholder-default literal" secret-assignment "$pw" || return 1
  local pin
  pin="$(printf '%s%s' 123 456)"
  reject_case "$tmp" "infra/pin.yml" "$(printf 'pin_password: %s' "$pin")" \
    "numeric secret value" secret-assignment "$pin" || return 1
  reject_case "$tmp" "src/Db.cs" "$(printf 'var cs = "Host=db;Username=app;Password = %s";' "$pw")" \
    "connection-string password" connstr-password "$pw" || return 1
  reject_case "$tmp" "src/Url.ts" "$(printf 'const u = "https://app:%s@example.invalid/";' "$pw")" \
    "URL userinfo" url-userinfo "$pw" || return 1
  reject_case "$tmp" "src/Key.cs" "$(printf 'public const string SigningKey = "%s";' "$b64")" \
    "base64 key material in C#" secret-key-material "$b64" || return 1
  reject_case "$tmp" "src/key.pem.txt.cs" "$(printf '// %s' "$pem")" \
    "PEM private key" pem-private-key "$pem" || return 1
  reject_case "$tmp" "src/aws.ts" "$(printf 'const id = "%s";' "$aws")" \
    "AWS access key id" aws-access-key-id "$aws" || return 1
  reject_case "$tmp" "src/gh.ts" "$(printf 'const t = "%s";' "$ghp")" \
    "GitHub token" github-token "$ghp" || return 1
  reject_case "$tmp" "src/slack.ts" "$(printf 'const s = "%s";' "$slack")" \
    "Slack token" slack-token "$slack" || return 1
  reject_case "$tmp" "src/jwt.ts" "$(printf 'const j = "%s";' "$jwt")" \
    "JWT" jwt "$jwt" || return 1

  # Scope: .github/ workflows, docker-compose*.yml, and tracked build-output
  # directories are scanned.
  reject_case "$tmp" ".github/workflows/ci.yml" "$(printf 'env:\n  NPM_TOKEN: %s' "$pw")" \
    ".github workflow literal" secret-assignment "$pw" || return 1
  reject_case "$tmp" "docker-compose.yml" "$(printf 'services:\n  db:\n    environment:\n      POSTGRES_PASSWORD: %s' "$pw")" \
    "docker-compose literal" secret-assignment "$pw" || return 1

  # Not skipped as binary: a NUL byte in the file does not hide the literal.
  printf '{"x": "a\000b"}\n{"password": "%s"}\n' "$pw" >"$tmp/infra/nul.json"
  expect_hit "$tmp" "file with a NUL byte" secret-assignment "$pw" || return 1
  rm -f "$tmp/infra/nul.json"

  # Fail closed: a file grep cannot read makes the scan exit 2, never pass.
  if [ "$(id -u)" -ne 0 ]; then
    printf '{"name": "x"}\n' >"$tmp/infra/locked.json"
    chmod 000 "$tmp/infra/locked.json"
    scan_tree "$tmp" 2>/dev/null
    chmod 600 "$tmp/infra/locked.json"
    rm -f "$tmp/infra/locked.json"
    if [ "$SCAN_RC" -ne 2 ]; then
      echo "self-test: FAILED — an unreadable file did not fail the scan (exit $SCAN_RC)" >&2
      return 1
    fi
    echo "self-test: unreadable file fails the scan (exit 2)"
  fi

  # git: a non-ASCII path is scanned; a tracked file under bin/ is scanned.
  if command -v git >/dev/null 2>&1; then
    git -C "$tmp" init -q
    local nonascii
    nonascii="$(printf 'src/Config/caf\303\251.json')"
    mkdir -p "$tmp/src/Config"
    printf '{\n  "password": "%s"\n}\n' "$pw" >"$tmp/$nonascii"
    git -C "$tmp" add -A
    expect_hit "$tmp" "non-ASCII tracked file name" secret-assignment "$pw" || return 1
    git -C "$tmp" rm -q --cached "$nonascii"
    rm -f "$tmp/$nonascii"

    mkdir -p "$tmp/infra/bin"
    printf 'export DB_PASSWORD=%s\n' "$pw" >"$tmp/infra/bin/run.sh"
    git -C "$tmp" add -A
    expect_hit "$tmp" "tracked file under bin/" secret-assignment "$pw" || return 1
    git -C "$tmp" rm -q --cached infra/bin/run.sh
    expect_clean "$tmp" "untracked bin/ build output skipped" || return 1
    rm -rf "$tmp/infra/bin"
    rm -rf "$tmp/.git"
  fi

  # Allowlist precision: an entry covers its reviewed line only; a different
  # literal in the same file, a nested path under a `*` glob and another rule
  # still fail.
  local dev
  dev="$(printf '%s-%s' reviewed dev)"
  printf 'APP_PASSWORD=%s\n' "$dev" >"$tmp/.env.dev.template"
  printf '%s\t%s\t%s\t%s\n' '.env.dev.template' secret-assignment 'self-test dev value' "^APP_PASSWORD=$dev\$" >"$ALLOW_FILE"
  printf '%s\t%s\t%s\n' 'src/*.ts' url-userinfo 'self-test glob' >>"$ALLOW_FILE"
  load_allowlist || return 1
  expect_clean "$tmp" "allowlisted reviewed line" || return 1
  printf 'APP_PASSWORD=%s\nAPI_TOKEN=%s\n' "$dev" "$pw" >"$tmp/.env.dev.template"
  expect_hit "$tmp" "new literal in an allowlisted file" secret-assignment "$pw" || return 1
  printf 'APP_PASSWORD=%s\n' "$dev" >"$tmp/.env.dev.template"
  reject_case "$tmp" "src/nested/url.ts" "$(printf 'const u = "https://app:%s@example.invalid/";' "$pw")" \
    "nested path under a * glob" url-userinfo "$pw" || return 1
  reject_case "$tmp" "src/aws2.ts" "$(printf 'const id = "%s";' "$aws")" \
    "other rule in an allowlisted file" aws-access-key-id "$aws" || return 1

  # A wildcard rule is a configuration error.
  printf '%s\t%s\t%s\n' 'src/x.ts' '*' 'wildcard' >"$ALLOW_FILE"
  if load_allowlist 2>/dev/null; then
    echo "self-test: FAILED — a wildcard allowlist rule was accepted" >&2
    return 1
  fi
  echo "self-test: wildcard allowlist rule refused"

  echo "self-test: passed"
  return 0
}

# --- entry point -------------------------------------------------------------

main() {
  local root="$ROOT" self=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --root)
        [ $# -ge 2 ] || { echo "check-no-secret-literals: --root needs a directory" >&2; usage >&2; exit 2; }
        root="$2"
        shift 2
        ;;
      --root=*)
        root="${1#--root=}"
        shift
        ;;
      --self-test)
        self=1
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        echo "check-no-secret-literals: unknown argument '$1'" >&2
        usage >&2
        exit 2
        ;;
    esac
  done

  if [ ! -d "$root" ]; then
    echo "check-no-secret-literals: no such directory: $root" >&2
    exit 2
  fi

  load_allowlist || exit 2

  if [ "$self" -eq 1 ]; then
    self_test
    exit $?
  fi

  local hits rc=0
  hits="$(run_scan "$root")" || rc=$?
  case "$rc" in
    0)
      echo "check-no-secret-literals: no literal secret in scope"
      exit 0
      ;;
    1)
      printf '%s\n' "$hits"
      echo "check-no-secret-literals: $(printf '%s\n' "$hits" | grep -c .) literal-secret hit(s)" >&2
      exit 1
      ;;
    *)
      exit 2
      ;;
  esac
}

main "$@"
