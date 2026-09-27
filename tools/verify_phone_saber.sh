#!/usr/bin/env bash
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"
UNITY_CANDIDATE="${UNITY_PROJECT_PATH:-$(cd "$REPO_ROOT/.." && pwd -P)/3D-Saber}"
if [[ -d "$UNITY_CANDIDATE" ]]; then
  UNITY_ROOT="$(cd "$UNITY_CANDIDATE" && pwd -P)"
else
  UNITY_ROOT="$UNITY_CANDIDATE"
fi

LOG_BASE="${PHONESABER_VERIFY_LOG_DIR:-$REPO_ROOT/.verify-logs/phone-saber}"
case "$LOG_BASE" in
  /*) ;;
  *) LOG_BASE="$REPO_ROOT/$LOG_BASE" ;;
esac
RUN_ID="$(date '+%Y%m%d-%H%M%S')"
RUN_DIR="$LOG_BASE/$RUN_ID"
suffix=1
while [[ -e "$RUN_DIR" ]]; do
  RUN_DIR="$LOG_BASE/$RUN_ID-$suffix"
  suffix=$((suffix + 1))
done
umask 077
if ! mkdir -p "$RUN_DIR"; then
  printf 'Could not create verification log directory: %s\n' "$RUN_DIR" >&2
  exit 2
fi

IOS_PROJECT="$REPO_ROOT/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj"
IOS_SCHEME="PhoneSaberSender"
IOS_SIMULATOR_ID="${PHONESABER_IOS_SIMULATOR_ID:-}"
IOS_SIMULATOR_DESTINATION=""
UNITY_VERSION=""
UNITY_EDITOR_PATH="${UNITY_EDITOR:-}"
UNITY_ROOT_VALID=false
UNITY_EDITOR_OPEN=false
UNITY_CLI_BLOCKED=false
UNITY_BLOCK_REASON=""
LAST_EXIT=0

IOS_XCTEST_STATUS="FAIL"
IOS_XCTEST_CLASSIFICATION="NOT_RUN"
DETECTION_STATUS="FAIL"
LOSSLESS_STATUS="FAIL"
TOOLS_STATUS="FAIL"
IOS_RELEASE_STATUS="FAIL"
UNITY_EDITMODE_STATUS="FAIL"
UNITY_PLAYMODE_STATUS="FAIL"
UNITY_COMPILE_STATUS="NOT_RUN"
UNITY_CAPABILITY_STATUS="FAIL"
DIFF_CHECK_STATUS="FAIL"
LOSSLESS_DETAIL=""

run_logged_command() {
  local label="$1"
  local key="$2"
  shift 2
  local command_file="$RUN_DIR/$key.command.log"
  local stdout_file="$RUN_DIR/$key.stdout.log"
  local stderr_file="$RUN_DIR/$key.stderr.log"

  {
    printf 'Step: %s\nCommand:' "$label"
    printf ' %q' "$@"
    printf '\n'
  } > "$command_file"
  "$@" > "$stdout_file" 2> "$stderr_file"
  LAST_EXIT=$?
  printf 'exit_code=%s\n' "$LAST_EXIT" >> "$command_file"
}

mark_not_run() {
  local label="$1"
  local key="$2"
  local status="$3"
  local reason="$4"
  printf 'Step: %s\nStatus: %s\nReason: %s\n' "$label" "$status" "$reason" > "$RUN_DIR/$key.command.log"
  : > "$RUN_DIR/$key.stdout.log"
  printf '%s\n' "$reason" > "$RUN_DIR/$key.stderr.log"
  LAST_EXIT=1
}

report_stage() {
  printf '%-20s %s\n' "$1" "$2"
}

unity_project_lock_in_logs() {
  local key="$1"
  local file
  for file in \
    "$RUN_DIR/$key.stdout.log" \
    "$RUN_DIR/$key.stderr.log" \
    "$RUN_DIR/$key.editor.log"; do
    if [[ -f "$file" ]] && grep -Eiq \
      'project is already open|already opened by another|project is locked|another Unity instance|project.*already.*running' \
      "$file"; then
      return 0
    fi
  done
  return 1
}

mark_unity_blocked() {
  local reason="$1"
  UNITY_CLI_BLOCKED=true
  UNITY_BLOCK_REASON="$reason"
  local key label
  for key in editmode playmode compile; do
    case "$key" in
      editmode) label="Unity EditMode" ;;
      playmode) label="Unity PlayMode" ;;
      compile) label="Unity Compile" ;;
    esac
    mark_not_run "$label" "unity-$key-blocked" "BLOCKED" "$reason"
    case "$key" in
      editmode) UNITY_EDITMODE_STATUS="BLOCKED" ;;
      playmode) UNITY_PLAYMODE_STATUS="BLOCKED" ;;
      compile) UNITY_COMPILE_STATUS="BLOCKED" ;;
    esac
  done
}

mark_remaining_unity_blocked() {
  local first="$1"
  local reason="$2"
  UNITY_CLI_BLOCKED=true
  UNITY_BLOCK_REASON="$reason"
  case "$first" in
    editmode)
      mark_not_run "Unity PlayMode" unity-playmode-blocked BLOCKED "$reason"
      mark_not_run "Unity Compile" unity-compile-blocked BLOCKED "$reason"
      UNITY_PLAYMODE_STATUS="BLOCKED"
      UNITY_COMPILE_STATUS="BLOCKED"
      ;;
    playmode)
      mark_not_run "Unity Compile" unity-compile-blocked BLOCKED "$reason"
      UNITY_COMPILE_STATUS="BLOCKED"
      ;;
  esac
}

validate_unity_test_result() {
  local label="$1"
  local key="$2"
  local result_file="$3"
  local validator
  validator='import sys, xml.etree.ElementTree as ET
path = sys.argv[1]
try:
    root = ET.parse(path).getroot()
except (OSError, ET.ParseError) as error:
    print("Invalid or missing Unity test result XML: %s" % error, file=sys.stderr)
    sys.exit(2)
if root.tag != "test-run":
    print("Unexpected Unity test result root: %s" % root.tag, file=sys.stderr)
    sys.exit(2)
total = int(root.attrib.get("total", root.attrib.get("testcasecount", "0")))
failed = int(root.attrib.get("failed", "0"))
skipped = int(root.attrib.get("skipped", "0"))
inconclusive = int(root.attrib.get("inconclusive", "0"))
result = root.attrib.get("result", "Unknown")
print("result=%s total=%d failed=%d skipped=%d inconclusive=%d" % (result, total, failed, skipped, inconclusive))
if result != "Passed" or total < 1 or failed or skipped or inconclusive:
    sys.exit(1)'
  run_logged_command "$label test-result validation" "$key-validation" \
    python3 -c "$validator" "$result_file"
}

validate_unity_compile_log() {
  local key="unity-compile-validation"
  local editor_log="$RUN_DIR/unity-compile.editor.log"
  local validator
  validator='import pathlib, re, sys
path = pathlib.Path(sys.argv[1])
if not path.is_file():
    print("Unity Editor log is missing: %s" % path, file=sys.stderr)
    sys.exit(2)
text = path.read_text(encoding="utf-8", errors="replace")
patterns = [r"error CS\d{4}\s*:", r"Scripts have compiler errors", r"Failed to compile scripts", r"Compilation failed:"]
hits = [line.strip() for line in text.splitlines() if any(re.search(pattern, line, re.I) for pattern in patterns)]
print("compiler_error_lines=%d" % len(hits))
for line in hits:
    print(line, file=sys.stderr)
sys.exit(1 if hits else 0)'
  run_logged_command "Unity compile-log validation" "$key" \
    python3 -c "$validator" "$editor_log"
}

run_unity_test() {
  local label="$1"
  local key="$2"
  local platform="$3"
  local test_filter="$4"
  local result_file="$RUN_DIR/$key.xml"
  local editor_log="$RUN_DIR/$key.editor.log"

  run_logged_command "$label" "$key" \
    "$UNITY_EDITOR_PATH" \
    -batchmode -nographics \
    -projectPath "$UNITY_ROOT" \
    -runTests \
    -testPlatform "$platform" \
    -testFilter "$test_filter" \
    -testResults "$result_file" \
    -logFile "$editor_log" \
    -quit
  local command_exit="$LAST_EXIT"

  if [[ "$command_exit" -ne 0 ]] && unity_project_lock_in_logs "$key"; then
    case "$key" in
      unity-editmode)
        UNITY_EDITMODE_STATUS="BLOCKED"
        mark_remaining_unity_blocked editmode "Unity Editor locked the project during CLI execution. Close the Editor and rerun the verification command."
        ;;
      unity-playmode)
        UNITY_PLAYMODE_STATUS="BLOCKED"
        mark_remaining_unity_blocked playmode "Unity Editor locked the project during CLI execution. Close the Editor and rerun the verification command."
        ;;
    esac
    return
  fi

  validate_unity_test_result "$label" "$key" "$result_file"
  local validation_exit="$LAST_EXIT"
  if [[ "$command_exit" -eq 0 && "$validation_exit" -eq 0 ]]; then
    case "$key" in
      unity-editmode) UNITY_EDITMODE_STATUS="PASS" ;;
      unity-playmode) UNITY_PLAYMODE_STATUS="PASS" ;;
    esac
  else
    case "$key" in
      unity-editmode) UNITY_EDITMODE_STATUS="FAIL" ;;
      unity-playmode) UNITY_PLAYMODE_STATUS="FAIL" ;;
    esac
  fi
}

printf 'PhoneSaber verification started\n'
printf 'Logs: %s\n' "$RUN_DIR"

# Pick an installed iPhone simulator unless the caller selected a specific UDID.
if [[ -z "$IOS_SIMULATOR_ID" ]]; then
  run_logged_command "iOS simulator discovery" ios-simulator-discovery \
    xcrun simctl list devices available -j
  if [[ "$LAST_EXIT" -eq 0 ]]; then
    selector='import json, sys
try:
    data = json.load(open(sys.argv[1], encoding="utf-8"))
except (OSError, ValueError) as error:
    print("Could not read simulator inventory: %s" % error, file=sys.stderr)
    sys.exit(2)
for runtime, devices in data.get("devices", {}).items():
    if not runtime.startswith("com.apple.CoreSimulator.SimRuntime.iOS"):
        continue
    for device in devices:
        if device.get("isAvailable") and device.get("name", "").startswith("iPhone"):
            print(device["udid"])
            sys.exit(0)
print("No available iPhone simulator was found", file=sys.stderr)
sys.exit(1)'
    run_logged_command "iOS simulator selection" ios-simulator-selection \
      python3 -c "$selector" "$RUN_DIR/ios-simulator-discovery.stdout.log"
    if [[ "$LAST_EXIT" -eq 0 ]]; then
      IOS_SIMULATOR_ID="$(cat "$RUN_DIR/ios-simulator-selection.stdout.log")"
    fi
  fi
fi
if [[ -n "$IOS_SIMULATOR_ID" ]]; then
  IOS_SIMULATOR_DESTINATION="platform=iOS Simulator,id=$IOS_SIMULATOR_ID"
  # Keep XCTest on one simulator and one worker for stable production verification.
  run_logged_command "iOS XCTest" ios-xctest \
    xcodebuild \
    -project "$IOS_PROJECT" \
    -scheme "$IOS_SCHEME" \
    -configuration Debug \
    -destination "$IOS_SIMULATOR_DESTINATION" \
    -derivedDataPath "${PHONESABER_VERIFY_DERIVED_DATA:-$RUN_DIR/iOS-XCTest-DerivedData}" \
    -resultBundlePath "$RUN_DIR/iOS-XCTest.xcresult" \
    -parallel-testing-enabled NO \
    -maximum-concurrent-test-simulator-destinations 1 \
    -maximum-parallel-testing-workers 1 \
    CODE_SIGNING_ALLOWED=NO \
    test
  xcodebuild_exit_code="$LAST_EXIT"

  run_logged_command "iOS XCTest result summary" ios-xctest-result-summary \
    xcrun xcresulttool get test-results summary \
    --path "$RUN_DIR/iOS-XCTest.xcresult"
  summary_exit_code="$LAST_EXIT"

  run_logged_command "iOS XCTest outcome classification" ios-xctest-classification \
    python3 "$REPO_ROOT/ios/PhoneSaberSender/Tools/xctest_result_classifier.py" \
    --summary "$RUN_DIR/ios-xctest-result-summary.stdout.log" \
    --summary-exit-code "$summary_exit_code" \
    --summary-stderr-log "$RUN_DIR/ios-xctest-result-summary.stderr.log" \
    --xcodebuild-exit-code "$xcodebuild_exit_code" \
    --xcodebuild-stdout-log "$RUN_DIR/ios-xctest.stdout.log" \
    --xcodebuild-stderr-log "$RUN_DIR/ios-xctest.stderr.log"
  IOS_XCTEST_CLASSIFICATION="$(sed -n 's/^classification=//p' "$RUN_DIR/ios-xctest-classification.stdout.log" | head -n 1)"
  if [[ "$IOS_XCTEST_CLASSIFICATION" == "PASS" || \
        "$IOS_XCTEST_CLASSIFICATION" == "PASS_WITH_WORKER_KILL" ]]; then
    IOS_XCTEST_STATUS="PASS"
  elif [[ -z "$IOS_XCTEST_CLASSIFICATION" ]]; then
    IOS_XCTEST_CLASSIFICATION="CLASSIFIER_ERROR"
  fi
else
  IOS_XCTEST_CLASSIFICATION="NOT_RUN"
  mark_not_run "iOS XCTest" ios-xctest FAIL "No iPhone Simulator UDID could be selected; inspect the simulator discovery and selection logs."
fi
printf '%-20s %s (%s)\n' "iOS XCTest" "$IOS_XCTEST_STATUS" "$IOS_XCTEST_CLASSIFICATION"

run_logged_command "Static BGRA Detection tests" detection-tests \
  bash "$REPO_ROOT/ios/PhoneSaberSender/run-static-tests.sh"
if [[ "$LAST_EXIT" -eq 0 ]]; then DETECTION_STATUS="PASS"; fi
report_stage "Detection" "$DETECTION_STATUS"

run_logged_command "Lossless regression" lossless-regression \
  python3 "$REPO_ROOT/ios/PhoneSaberSender/Tools/run_lossless_regression.py" \
  --json "$RUN_DIR/lossless-regression.json" \
  --csv "$RUN_DIR/lossless-regression.csv"
if [[ "$LAST_EXIT" -eq 0 ]]; then
  lossless_summary='import json, sys
try:
    data = json.load(open(sys.argv[1], encoding="utf-8"))
    summary = data["summary"]
    fixture_count = int(summary["fixture_count"])
    skipped = summary.get("skipped_optional", [])
except (OSError, ValueError, KeyError, TypeError) as error:
    print("Could not read lossless regression summary: %s" % error, file=sys.stderr)
    sys.exit(2)
if fixture_count < 1:
    print("Lossless regression ran no fixtures", file=sys.stderr)
    sys.exit(1)
print("fixtures=%d optional_manifest_fixtures_absent=%d" % (fixture_count, len(skipped)))
if skipped:
    print("absent_optional_fixtures=" + ",".join(skipped))'
  run_logged_command "Lossless regression summary validation" lossless-summary \
    python3 -c "$lossless_summary" "$RUN_DIR/lossless-regression.json"
  if [[ "$LAST_EXIT" -eq 0 ]]; then
    LOSSLESS_STATUS="PASS"
    LOSSLESS_DETAIL="$(cat "$RUN_DIR/lossless-summary.stdout.log")"
  fi
fi
report_stage "Lossless" "$LOSSLESS_STATUS"
if [[ -n "$LOSSLESS_DETAIL" ]]; then
  printf '  %s\n' "$(head -n 1 "$RUN_DIR/lossless-summary.stdout.log")"
fi

run_logged_command "PhoneSaber Tools unittest" tools-unittest \
  python3 -B -m unittest discover \
  -s "$REPO_ROOT/ios/PhoneSaberSender/Tools" \
  -p 'test_*.py' \
  -v
if [[ "$LAST_EXIT" -eq 0 ]]; then TOOLS_STATUS="PASS"; fi
report_stage "Tools" "$TOOLS_STATUS"

run_logged_command "iOS Release build" ios-release-build \
  xcodebuild \
  -project "$IOS_PROJECT" \
  -scheme "$IOS_SCHEME" \
  -configuration Release \
  -destination 'generic/platform=iOS' \
  -derivedDataPath "$RUN_DIR/iOS-Release-DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  build
if [[ "$LAST_EXIT" -eq 0 ]]; then IOS_RELEASE_STATUS="PASS"; fi
report_stage "iOS Release" "$IOS_RELEASE_STATUS"

# Resolve the Unity project independently and detect a GUI Editor that owns it.
if [[ -f "$UNITY_ROOT/ProjectSettings/ProjectVersion.txt" ]]; then
  run_logged_command "Unity project Git root" unity-project-git-root \
    git -C "$UNITY_ROOT" rev-parse --show-toplevel
  if [[ "$LAST_EXIT" -eq 0 ]]; then
    UNITY_GIT_ROOT="$(cat "$RUN_DIR/unity-project-git-root.stdout.log")"
    if [[ "$UNITY_GIT_ROOT" == "$UNITY_ROOT" ]]; then UNITY_ROOT_VALID=true; fi
  fi
fi

if [[ "$UNITY_ROOT_VALID" == true ]]; then
  UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$UNITY_ROOT/ProjectSettings/ProjectVersion.txt" | head -n 1)"
  if [[ -z "$UNITY_EDITOR_PATH" ]]; then
    UNITY_EDITOR_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
  fi

  editor_pids_file="$RUN_DIR/unity-editor-pids.stdout.log"
  editor_check_err="$RUN_DIR/unity-editor-pids.stderr.log"
  printf 'Command: pgrep -x Unity; inspect matching PID command lines for the selected Unity project (command lines are not saved)\n' \
    > "$RUN_DIR/unity-editor-pids.command.log"
  : > "$editor_check_err"
  /usr/bin/pgrep -x Unity > "$editor_pids_file" 2> "$editor_check_err"
  pgrep_exit=$?
  if [[ "$pgrep_exit" -gt 1 ]]; then
    printf 'exit_code=%s\n' "$pgrep_exit" >> "$RUN_DIR/unity-editor-pids.command.log"
    mark_unity_blocked "Could not confirm whether Unity Editor is open; see the Unity process-detection logs."
  else
    if [[ "$pgrep_exit" -eq 1 ]]; then : > "$editor_pids_file"; fi
    printf 'exit_code=0\n' >> "$RUN_DIR/unity-editor-pids.command.log"
    : > "$RUN_DIR/unity-editor-check.stdout.log"
    : > "$RUN_DIR/unity-editor-check.stderr.log"
    while IFS= read -r pid; do
      [[ -n "$pid" ]] || continue
      process_command="$(/bin/ps -p "$pid" -o command= 2>> "$RUN_DIR/unity-editor-check.stderr.log")"
      ps_exit=$?
      if [[ "$ps_exit" -ne 0 ]]; then continue; fi
      case "$process_command" in
        *"$UNITY_ROOT"*)
          case "$process_command" in
            *-batchmode*|*-batchMode*) ;;
            *)
              UNITY_EDITOR_OPEN=true
              printf 'Unity Editor owns target project: pid=%s\n' "$pid" \
                >> "$RUN_DIR/unity-editor-check.stdout.log"
              ;;
          esac
          ;;
      esac
    done < "$editor_pids_file"
    printf 'exit_code=0\n' > "$RUN_DIR/unity-editor-check.command.log"
  fi

  if [[ "${PHONESABER_VERIFY_UNITY_RUN:-0}" != "1" ]]; then
    if [[ ! -x "$UNITY_EDITOR_PATH" ]]; then
      UNITY_CAPABILITY_STATUS="BLOCKED"
      UNITY_BLOCK_REASON="Unity Editor executable is unavailable at $UNITY_EDITOR_PATH."
    elif [[ "$UNITY_EDITOR_OPEN" == true || "$UNITY_CLI_BLOCKED" == true ]]; then
      UNITY_CAPABILITY_STATUS="BLOCKED"
      UNITY_BLOCK_REASON="Unity Editor currently owns the project."
    else
      UNITY_CAPABILITY_STATUS="CAPABLE"
    fi
    UNITY_EDITMODE_STATUS="NOT_RUN"
    UNITY_PLAYMODE_STATUS="NOT_RUN"
    UNITY_COMPILE_STATUS="NOT_RUN"
  elif [[ "$UNITY_EDITOR_OPEN" == true ]]; then
    UNITY_CAPABILITY_STATUS="BLOCKED"
    mark_unity_blocked "Unity Editor already has 3D-Saber open. Close it and rerun to execute EditMode, PlayMode, and compile through the CLI."
  elif [[ "$UNITY_CLI_BLOCKED" == true ]]; then
    UNITY_CAPABILITY_STATUS="BLOCKED"
    : "Unity process detection did not complete; the blocked status is retained."
  elif [[ ! -x "$UNITY_EDITOR_PATH" ]]; then
    UNITY_CAPABILITY_STATUS="BLOCKED"
    mark_not_run "Unity EditMode" unity-editmode-unavailable FAIL "Unity Editor executable is not available at $UNITY_EDITOR_PATH."
    mark_not_run "Unity PlayMode" unity-playmode-unavailable FAIL "Unity Editor executable is not available at $UNITY_EDITOR_PATH."
    mark_not_run "Unity Compile" unity-compile-unavailable FAIL "Unity Editor executable is not available at $UNITY_EDITOR_PATH."
  else
    UNITY_CAPABILITY_STATUS="CAPABLE"
    run_unity_test "Unity PhoneSaber EditMode" unity-editmode EditMode \
      'InputPointConversionTests;InputPointSingletonTests;PhoneSaberProjectSettingsTests;SaberInputBridgeTests'
    if [[ "$UNITY_EDITMODE_STATUS" == "BLOCKED" ]]; then
      : "Unity project lock stopped further CLI work."
    elif [[ "$UNITY_CLI_BLOCKED" == true ]]; then
      : "Unity CLI lock state is already recorded."
    else
      report_stage "Unity EditMode" "$UNITY_EDITMODE_STATUS"
      run_unity_test "Unity PhoneSaber PlayMode" unity-playmode PlayMode \
        'PhoneSaberReliabilityPlayTests'
      if [[ "$UNITY_PLAYMODE_STATUS" == "BLOCKED" ]]; then
        : "Unity project lock stopped compile CLI work."
      elif [[ "$UNITY_CLI_BLOCKED" == true ]]; then
        : "Unity CLI lock state is already recorded."
      else
        report_stage "Unity PlayMode" "$UNITY_PLAYMODE_STATUS"
        run_logged_command "Unity compile" unity-compile \
          "$UNITY_EDITOR_PATH" \
          -batchmode -nographics \
          -projectPath "$UNITY_ROOT" \
          -logFile "$RUN_DIR/unity-compile.editor.log" \
          -quit
        compile_command_exit="$LAST_EXIT"
        validate_unity_compile_log
        compile_validation_exit="$LAST_EXIT"
        if [[ "$compile_command_exit" -eq 0 && "$compile_validation_exit" -eq 0 ]]; then
          UNITY_COMPILE_STATUS="PASS"
        elif [[ "$compile_command_exit" -ne 0 ]] && unity_project_lock_in_logs unity-compile; then
          UNITY_COMPILE_STATUS="BLOCKED"
          UNITY_CLI_BLOCKED=true
          UNITY_BLOCK_REASON="Unity Editor locked the project during CLI compilation. Close the Editor and rerun the verification command."
        fi
      fi
    fi
  fi
else
  UNITY_CAPABILITY_STATUS="FAIL"
  mark_not_run "Unity EditMode" unity-editmode-invalid-project FAIL "UNITY_PROJECT_PATH does not identify the 3D-Saber Git project; expected Assets/, Packages/, and ProjectSettings/."
  mark_not_run "Unity PlayMode" unity-playmode-invalid-project FAIL "UNITY_PROJECT_PATH does not identify the 3D-Saber Git project; expected Assets/, Packages/, and ProjectSettings/."
  mark_not_run "Unity Compile" unity-compile-invalid-project FAIL "UNITY_PROJECT_PATH does not identify the 3D-Saber Git project; expected Assets/, Packages/, and ProjectSettings/."
fi

if [[ "${PHONESABER_VERIFY_UNITY_RUN:-0}" == "1" && ( "$UNITY_EDITOR_OPEN" == true || "$UNITY_CLI_BLOCKED" == true ) ]]; then
  [[ "$UNITY_EDITMODE_STATUS" != "FAIL" ]] || UNITY_EDITMODE_STATUS="BLOCKED"
  [[ "$UNITY_PLAYMODE_STATUS" != "FAIL" ]] || UNITY_PLAYMODE_STATUS="BLOCKED"
  [[ "$UNITY_COMPILE_STATUS" != "FAIL" ]] || UNITY_COMPILE_STATUS="BLOCKED"
fi
report_stage "Unity EditMode" "$UNITY_EDITMODE_STATUS"
report_stage "Unity PlayMode" "$UNITY_PLAYMODE_STATUS"
report_stage "Unity Compile" "$UNITY_COMPILE_STATUS"
report_stage "Unity capability" "$UNITY_CAPABILITY_STATUS"

run_logged_command "school-festival git diff --check" diff-check-school-festival \
  git -C "$REPO_ROOT" diff --check
school_diff_exit="$LAST_EXIT"
if [[ "$school_diff_exit" -eq 0 ]]; then DIFF_CHECK_STATUS="PASS"; fi
report_stage "Diff Check" "$DIFF_CHECK_STATUS"

printf '\nPhoneSaber verification summary\n'
printf '%-20s %s\n' "iOS XCTest" "$IOS_XCTEST_STATUS"
printf '%-20s %s\n' "Detection" "$DETECTION_STATUS"
if [[ -n "$LOSSLESS_DETAIL" ]]; then
  printf '%-20s %s (%s)\n' "Lossless" "$LOSSLESS_STATUS" "$(head -n 1 "$RUN_DIR/lossless-summary.stdout.log")"
else
  printf '%-20s %s\n' "Lossless" "$LOSSLESS_STATUS"
fi
printf '%-20s %s\n' "Tools" "$TOOLS_STATUS"
printf '%-20s %s\n' "iOS Release" "$IOS_RELEASE_STATUS"
printf '%-20s %s\n' "Unity EditMode" "$UNITY_EDITMODE_STATUS"
printf '%-20s %s\n' "Unity PlayMode" "$UNITY_PLAYMODE_STATUS"
printf '%-20s %s\n' "Unity Compile" "$UNITY_COMPILE_STATUS"
printf '%-20s %s\n' "Unity capability" "$UNITY_CAPABILITY_STATUS"
printf '%-20s %s\n' "Diff Check" "$DIFF_CHECK_STATUS"
if [[ -n "$UNITY_BLOCK_REASON" ]]; then printf 'Unity: %s\n' "$UNITY_BLOCK_REASON"; fi
printf 'Logs: %s\n' "$RUN_DIR"

for status in \
  "$IOS_XCTEST_STATUS" "$DETECTION_STATUS" "$LOSSLESS_STATUS" "$TOOLS_STATUS" \
  "$IOS_RELEASE_STATUS" "$UNITY_EDITMODE_STATUS" "$UNITY_PLAYMODE_STATUS" \
  "$UNITY_COMPILE_STATUS" "$UNITY_CAPABILITY_STATUS" "$DIFF_CHECK_STATUS"; do
  if [[ "$status" == "FAIL" ]]; then exit 1; fi
done
if [[ "$IOS_XCTEST_STATUS" == "BLOCKED" || "$DETECTION_STATUS" == "BLOCKED" || \
      "$LOSSLESS_STATUS" == "BLOCKED" || "$TOOLS_STATUS" == "BLOCKED" || \
      "$IOS_RELEASE_STATUS" == "BLOCKED" || "$UNITY_EDITMODE_STATUS" == "BLOCKED" || \
      "$UNITY_PLAYMODE_STATUS" == "BLOCKED" || "$UNITY_COMPILE_STATUS" == "BLOCKED" || \
      "$UNITY_CAPABILITY_STATUS" == "BLOCKED" || "$DIFF_CHECK_STATUS" == "BLOCKED" ]]; then
  exit 2
fi
exit 0
