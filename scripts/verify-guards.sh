# The guard and selection half of scripts/verify.sh: the refusal of a disarmed
# gate (U8, docs/engineering/agent-code-rules.md), the trait-stage runner,
# argument parsing and the consumer of the gate-scope report. Split out by
# responsibility because verify.sh's stage table alone is near the 500-line file
# limit; verify.sh sets `set -euo pipefail` and ROOT, then sources this file before
# anything runs, so the functions here rely on ROOT, ALL_STAGES and usage() from
# there. Sourced, never executed: no shebang and no shell options of its own.

# --- disarmed-gate refusal ----------------------------------------------------
# U8: a gate is never disarmed. dotnet's ignore flag turns a run that executed
# nothing into a success, so it is refused in this script's own source, in the
# caller's arguments and in the environment the stages inherit. It is spelled once
# below, in two halves: a scan of these files for the flag would otherwise read
# the scanner's own value as a hand-over. Everything else compares the variable,
# so no other line of this file mentions it.
U8_GATE_FLAG='--ignore-exit'"-code"

# The per-line judgement `u8_flag_lines` runs, kept as a program of its own so
# the shell and awk parts each stay readable. It first reads the words of the
# leading command (up to the first control operator) to find the pattern operand
# a `grep`/`rg` search reads, then walks the line for occurrences and exempts
# only those inside that operand.
read -r -d '' U8_SCAN_AWK <<'AWK' || true
BEGIN {
  sq = sprintf("%c", 39); dq = sprintf("%c", 34)
  n = split("A B C D E M T d f g j m r t", word, " ")
  for (k = 1; k <= n; k++) SHORT_ARG[word[k]] = 1
  n = split("after-context before-context context max-count max-columns" \
            " directories devices label binary-files file include exclude" \
            " exclude-dir exclude-from replace encoding glob type type-not" \
            " threads sort sortr ignore-file pre pre-glob", word, " ")
  for (k = 1; k <= n; k++) LONG_ARG[word[k]] = 1
}
function unquote(text,   n) {
  n = length(text)
  if (n < 2) return text
  if ((substr(text, 1, 1) == sq && substr(text, n, 1) == sq) ||
      (substr(text, 1, 1) == dq && substr(text, n, 1) == dq))
    return substr(text, 2, n - 2)
  return text
}
# Does this single-letter or long option read the next word as its own value?
# `-e`/`--regexp` are excluded: their value is the pattern, exempted instead.
function takes_argument(option,   name, core) {
  name = option
  sub(/=.*$/, "", name)
  if (name == "-e" || name == "--regexp") return 0
  if (substr(name, 1, 2) == "--") return (name in LONG_ARG)
  if (length(name) != 2) return 0
  core = substr(name, 2, 1)
  return (core in SHORT_ARG)
}
function in_pattern(at) {
  return pattern_start > 0 && at >= pattern_start && at <= pattern_end
}
# An occurrence is exempt only inside the pattern operand, or on the right side
# of a `[[ … == … ]]` comparison, and in both cases only outside a substitution:
# a command inside `$( … )`, `<( … )` or backticks hands the flag itself.
function exempt(depth, tick, bracket, compared, at) {
  if (depth > 0 || tick) return 0
  if (bracket && compared) return 1
  return in_pattern(at)
}
{
  line = $0
  size = length(line)
  ntok = 0; pattern_start = 0; pattern_end = -1; i = 1
  while (i <= size) {
    ch = substr(line, i, 1); two = substr(line, i, 2)
    if (ch == " " || ch == "\t") { i++; continue }
    if (two == "&&" || two == "||" || ch == ";" || ch == "|" || ch == "&") break
    start = i; quote = ""
    while (i <= size) {
      ch = substr(line, i, 1); two = substr(line, i, 2)
      if (quote != "") { if (ch == quote) { quote = "" }; i++; continue }
      if (ch == sq || ch == dq) { quote = ch; i++; continue }
      if (ch == " " || ch == "\t") break
      if (two == "&&" || two == "||" || ch == ";" || ch == "|" || ch == "&") break
      if (two == "$(" || two == "<(" || two == ">(") break
      i++
    }
    if (i == start) break
    ntok++
    wstart[ntok] = start; wend[ntok] = i - 1; wtext[ntok] = substr(line, start, i - start)
  }
  if (ntok > 0 && (unquote(wtext[1]) == "grep" || unquote(wtext[1]) == "rg")) {
    for (k = 2; k <= ntok; k++) {
      w = unquote(wtext[k])
      if (w == "--" || w == "-e" || w == "--regexp") {
        if (k < ntok) { pattern_start = wstart[k + 1]; pattern_end = wend[k + 1] }
        break
      }
      if (w ~ /^--regexp=/) { pattern_start = wstart[k] + 9; pattern_end = wend[k]; break }
      if (w ~ /^-e./) { pattern_start = wstart[k] + 2; pattern_end = wend[k]; break }
      if (w ~ /^-/) { if (takes_argument(w)) k++; continue }
      pattern_start = wstart[k]; pattern_end = wend[k]; break
    }
  }
  quote = ""; depth = 0; tick = 0; bracket = 0; compared = 0
  handed = 0; i = 1
  while (i <= size) {
    ch = substr(line, i, 1)
    two = substr(line, i, 2)
    if (quote == sq) {
      if (ch == sq) { quote = ""; i++; continue }
    } else if (quote == dq) {
      if (ch == dq) { quote = ""; i++; continue }
      if (ch == "`") { tick = (tick + 1) % 2; i++; continue }
      if (two == "$(" || two == "<(" || two == ">(") { depth++; i += 2; continue }
      if (ch == ")" && depth > 0) { depth--; i++; continue }
    } else if (ch == "`") {
      tick = (tick + 1) % 2; i++; continue
    } else if (ch == sq) { quote = sq; i++; continue
    } else if (ch == dq) { quote = dq; i++; continue
    } else if (two == "$(" || two == "<(" || two == ">(") { depth++; i += 2; continue
    } else if (ch == ")" && depth > 0) { depth--; i++; continue
    } else if (two == "[[") { bracket = 1; compared = 0; i += 2; continue
    } else if (two == "]]") { bracket = 0; compared = 0; i += 2; continue
    } else if (two == "==") { if (bracket) { compared = 1 }; i += 2; continue }
    if (substr(line, i, length(flag)) == flag) {
      if (exempt(depth, tick, bracket, compared, i)) { i += length(flag); continue }
      handed = 1
      break
    }
    i++
  }
  if (handed) printf "%s:%d: %s\n", name, NR, line
}
AWK

# u8_flag_lines <file>: every logical line that hands the flag to a command.
# Continuation lines are joined first, so a flag on the line after `dotnet test`
# is judged with its command. An occurrence is exempt in exactly two places: as
# the pattern operand of the `grep`/`rg` command the line starts with (the word
# after `--`, `-e` or `--regexp=`, or the first non-option word), or on the
# right-hand side of a `[[ … == … ]]` comparison. Anywhere else the line is
# refused: another operand or option of the same search, anything after `&&` or
# `;`, and any command inside `$( … )`, `<( … )` or backticks, even in a
# `[[ … ]]` test. A second word on the line never borrows the first one's
# exemption, and an unsupported option shape is refused rather than guessed.
u8_flag_lines() {
  sed -e :a -e '/\\$/N; s/\\\n/ /; ta' "$1" |
    awk -v flag="$U8_GATE_FLAG" -v name="$1" "$U8_SCAN_AWK"
}

refuse_gate_flag() {
  local argument variable value file found=() hits
  for file in "$ROOT/scripts/verify.sh" "$ROOT/scripts/verify-guards.sh"; do
    hits="$(u8_flag_lines "$file")"
    if [[ -n "$hits" ]]; then found+=("$hits"); fi
  done
  if [[ "${#found[@]}" -gt 0 ]]; then
    echo "verify: refusing to run: a line of this script hands the ignore flag to a command" >&2
    printf '%s\n' "${found[@]}" >&2
    exit 2
  fi
  for argument in "$@"; do
    if [[ "$argument" == *"$U8_GATE_FLAG"* ]]; then
      echo "verify: refusing the ignore flag in the arguments: a gate is never disarmed" >&2
      exit 2
    fi
  done
  # Exported variables are read by value, never by line: a value that spans lines
  # is one value, and env output split per line would read its second line as a
  # new variable and miss the flag there.
  while IFS= read -r variable; do
    value="${!variable-}"
    if [[ "$variable" == "TESTINGPLATFORM_EXITCODE_IGNORE" ]]; then
      echo "verify: refusing TESTINGPLATFORM_EXITCODE_IGNORE in the environment:" \
        "a gate is never disarmed from outside" >&2
      exit 2
    fi
    if [[ "$value" == *"$U8_GATE_FLAG"* ]]; then
      echo "verify: refusing the ignore flag in \$${variable} from the environment:" \
        "a gate is never disarmed from outside" >&2
      exit 2
    fi
  done < <(compgen -e)
}

# --- trait-selected stages ----------------------------------------------------
# owning_project <file>: the nearest directory above the file that holds a
# *.csproj — the project that actually builds it. `dirname` alone would name a
# subfolder of a test project once a trait-bearing test moves into one.
owning_project() {
  local dir
  dir="$(dirname "$1")"
  while [[ -n "$dir" && "$dir" != "." ]]; do
    if compgen -G "$dir/*.csproj" >/dev/null; then
      printf '%s\n' "$dir"
      return 0
    fi
    dir="$(dirname "$dir")"
  done
  return 1
}

# The attribute reader `trait_files` runs: each `Trait(` is matched to its
# closing paren, so the category and the trait must be arguments of one attribute.
read -r -d '' TRAIT_AWK <<'AWK' || true
{
  text = $0
  size = length(text)
  pos = 1
  while (pos <= size && match(substr(text, pos), /Trait[[:space:]]*\(/)) {
    lead = pos + RSTART - 2
    if (lead >= 1 && substr(text, lead, 1) ~ /[A-Za-z0-9_]/) { pos = pos + RSTART + RLENGTH - 1; continue }
    depth = 1
    j = pos + RSTART + RLENGTH - 1
    inner = ""
    while (j <= size && depth > 0) {
      ch = substr(text, j, 1)
      if (ch == "(") { depth++ }
      else if (ch == ")") { depth--; if (depth == 0) break }
      inner = inner ch
      j++
    }
    if (index(inner, "Category") > 0 && index(inner, trait) > 0) { found = 1; break }
    pos = j + 1
  }
  if (found) print "trait"
}
AWK

# trait_files <trait>: every *.cs file under tests/ naming Category=<trait> in a
# `Trait( … )` attribute. The attribute is read as text, so whitespace between
# its tokens, a line break inside it and a constant (`nameof(Hostile)`) instead
# of a string literal all still count: a reformatted test cannot slip out of the
# stage by changing its spelling.
trait_files() {
  local trait="$1" file
  while IFS= read -r file; do
    if [[ -n "$(tr '\n' ' ' <"$file" | awk -v trait="$trait" "$TRAIT_AWK")" ]]; then
      printf '%s\n' "$file"
    fi
  done < <(grep -rlE --include='*.cs' 'Trait[[:space:]]*\(' tests || true)
}

# run_trait_stage <trait> <assembly>...
# Each named assembly runs its own `dotnet test --project`: zero matching tests
# then fail as exit 8 for that assembly, and `--minimum-expected-tests 1` states
# the same rule where the exit code cannot (a stage that selected nothing). A
# trait-bearing file whose owning project is not named fails the stage, so a
# moved or reformatted test cannot silently drop out of it.
run_trait_stage() {
  local trait="$1" file project
  shift
  while IFS= read -r file; do
    if ! project="$(owning_project "$file")"; then
      echo "verify: $file carries the $trait trait, but no *.csproj owns it — add the" \
        "project that holds it to the trait's assembly list" >&2
      exit 2
    fi
    if ! in_list "$project" "$@"; then
      echo "verify: $file carries the $trait trait, but $project is not run by this stage" \
        "— add it to the trait's assembly list" >&2
      exit 2
    fi
  done < <(trait_files "$trait")
  for project in "$@"; do
    dotnet test --no-build -c Release --project "$project" \
      --filter-trait "Category=$trait" --minimum-expected-tests 1
  done
}

# --- argument parsing ---------------------------------------------------------
# `in_list <name> <names...>`: membership over an explicit list.
in_list() {
  local needle="$1" item
  shift
  for item in "$@"; do [[ "$item" == "$needle" ]] && return 0; done
  return 1
}

fail_usage() {
  echo "verify: $1" >&2
  usage >&2
  exit 2
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
    --stage)
      if [[ $# -lt 2 ]]; then fail_usage "--stage needs a stage name"; fi
      # An explicit list is the caller's own verdict: every named stage runs,
      # whatever the tier would have chosen; an unknown name is a caller typo.
      if ! in_list "$2" "${ALL_STAGES[@]}"; then fail_usage "unknown stage '$2'"; fi
      selected+=("$2")
      shift 2
      ;;
    --list-stages) printf '%s\n' "${ALL_STAGES[@]}"; exit 0 ;;
    -h | --help) usage; exit 0 ;;
    *) fail_usage "unknown argument '$1'" ;;
    esac
  done
}

# --- path-scoped tiers --------------------------------------------------------
# No --stage means "whatever this change set can fail": scripts/gate_scope.py is
# the only decider (and where it is tested), this block just consumes its report.
# A classifier that crashes, prints nothing or omits the tier is the full gate.
#
# The FIRST line for a key wins: a duplicate may repeat a fact, never replace it.
read_scope() {
  local line
  while IFS= read -r line; do
    case "$line" in
    "tier: "*) if [[ -z "$scope_tier" ]]; then scope_tier="${line#tier: }"; fi ;;
    "reason: "*) if [[ -z "$scope_reason" ]]; then scope_reason="${line#reason: }"; fi ;;
    "changed: "*) if [[ -z "$scope_changed" ]]; then scope_changed="${line#changed: }"; fi ;;
    "base: "*) if [[ -z "$scope_base" ]]; then scope_base="${line#base: }"; fi ;;
    "stages: "*) if [[ -z "$scope_stages" ]]; then scope_stages="${line#stages: }"; fi ;;
    "skipped: "*) if [[ -z "$scope_skipped" ]]; then scope_skipped="${line#skipped: }"; fi ;;
    "note: "*) scope_notes+=("${line#note: }") ;;
    esac
  done <<<"$1"
}

# The scope line names the tier and the change set it was read from: the same
# facts for a narrowed run and for a full one, so a reader can tell "nothing
# needed narrowing" from "the classifier said nothing".
print_scope_line() {
  local note
  echo "== gate scope: ${scope_tier} (${scope_reason}, ${scope_changed} changed path(s), base ${scope_base})"
  if [[ -n "$scope_skipped" ]]; then echo "   skipped by scope: ${scope_skipped}"; fi
  for note in ${scope_notes[@]+"${scope_notes[@]}"}; do echo "   note: ${note}"; done
}

# Turn the parsed report into the run list. Only a `docs` or `no-dotnet` tier
# with a non-empty list of known stages may narrow a bare run; an unknown tier,
# an empty or invented stage list, or a missing key is the full gate.
plan_selection() {
  local name narrowed="" candidates=() reported_full=""
  case "$scope_tier" in
  docs | no-dotnet) if [[ -n "$scope_stages" ]]; then narrowed=1; fi ;;
  esac
  if [[ -n "$narrowed" ]]; then
    read -r -a candidates <<<"$scope_stages" || true
    for name in ${candidates[@]+"${candidates[@]}"}; do
      if ! in_list "$name" "${ALL_STAGES[@]}"; then narrowed=""; fi
    done
    if [[ "${#candidates[@]}" -eq 0 ]]; then narrowed=""; fi
  fi
  if [[ -n "$narrowed" ]]; then
    selected=("${candidates[@]}")
    print_scope_line
  else
    # A `full` verdict the classifier made is still a verdict with a reason: the
    # change set was read and needed every stage. A report that cannot narrow for
    # any other reason — no output, a crash, an unknown tier, a stage name the
    # table lacks — leaves no stage list to report, and says so.
    if [[ "$scope_tier" == "full" && -n "$scope_reason" && -n "$scope_changed" && -n "$scope_base" ]]; then
      reported_full=1
    fi
    scope_tier="full"
    if [[ -n "$reported_full" ]]; then print_scope_line; else
      echo "== gate scope: no usable tier/stage list (full gate)"
    fi
    selected=("${ALL_STAGES[@]}")
  fi
}

# The success line is a claim about the whole gate, so it is made only when every
# canonical stage is in the run list.
report_result() {
  local name
  for name in "${ALL_STAGES[@]}"; do
    if ! in_list "$name" ${selected[@]+"${selected[@]}"}; then
      echo "== verify: selected stages passed: ${selected[*]}"
      return 0
    fi
  done
  echo "== verify: all stages passed"
}
