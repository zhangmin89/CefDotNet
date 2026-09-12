import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("ci_diagnostics", ROOT / ".github/scripts/summarize-ci-diagnostics.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class CiDiagnosticsTests(unittest.TestCase):
    def test_warning_deduplication_preserves_different_locations(self):
        entries = [("build.log", "\n".join([
            r"D:\a\repo\src\Example.cs(4,8): warning CS8602: Possible null. [D:\a\repo\App.csproj]",
            "/home/runner/repo/src/Example.cs(4,8): warning CS8602: Possible null. [/home/runner/repo/App.csproj]",
            "/home/runner/repo/src/Example.cs(5,8): warning CS8602: Possible null. [/home/runner/repo/App.csproj]",
        ]))]
        result = MODULE.summarize(entries, ROOT)
        self.assertEqual(2, result["unique_warning_count"])
        self.assertEqual([2, 1], [row["occurrences"] for row in result["warnings"]])
        self.assertEqual("Possible null.", result["warnings"][0]["message"])

    def test_phase_timing_ignores_stdout_duplicates_and_interleaved_tests(self):
        contents = "\n".join([
            "ticks=100 frequency=10 pid=1 thread=3 test=A stage=setup-start",
            "ticks=110 frequency=10 pid=1 thread=4 test=B stage=setup-start",
            "ticks=200 frequency=10 pid=1 thread=4 test=A stage=setup-complete",
            "ticks=220 frequency=10 pid=1 thread=3 test=A stage=teardown-start",
        ])
        result = MODULE.summarize([("test-phases-1.log", contents), ("test.stdout.log", contents)], ROOT)
        self.assertEqual([10.0, 2.0], [row["seconds"] for row in result["slowest_phase_intervals"]])

    def test_phase_timing_uses_timestamps_when_threads_write_out_of_order(self):
        contents = "\n".join([
            "ticks=537042750000 frequency=1000000000 pid=6154 thread=19 test=A stage=resource-handler-start",
            "ticks=537042760239 frequency=1000000000 pid=6154 thread=21 test=A stage=fetch-script-complete",
            "ticks=537042756167 frequency=1000000000 pid=6154 thread=19 test=A stage=resource-handler-returned",
        ])
        result = MODULE.summarize([("test-phases-6154.log", contents)], ROOT)
        phases = result["slowest_phase_intervals"]
        self.assertEqual(2, len(phases))
        self.assertEqual(("resource-handler-start", "resource-handler-returned"), (phases[0]["start"], phases[0]["end"]))
        self.assertAlmostEqual(0.000006167, phases[0]["seconds"])
        self.assertEqual(("resource-handler-returned", "fetch-script-complete"), (phases[1]["start"], phases[1]["end"]))
        self.assertAlmostEqual(0.000004072, phases[1]["seconds"])

    def test_phase_timing_rejects_inconsistent_clock_frequencies(self):
        contents = "\n".join([
            "ticks=100 frequency=10 pid=1 thread=3 test=A stage=start",
            "ticks=200 frequency=20 pid=1 thread=4 test=A stage=end",
        ])
        with self.assertRaisesRegex(ValueError, "Invalid monotonic phase timestamps"):
            MODULE.summarize([("test-phases-1.log", contents)], ROOT)

    def test_slowest_commands_deduplicates_attachment_copies(self):
        data = dict(Name="neutral-pack", PID=42, ExitCode=0, DurationSeconds=78.9, Arguments=["pack", "F:\\repo\\src\\CefGlue.Common.csproj"])
        entries = [
            ("artifacts/browser-process-nunit-0123456789abcdef0123456789abcdef/logs/02-neutral-pack.command.json", dict(data)),
            ("test-results.zip/In/0123456789abcdef0123456789abcdef/ZM/02-neutral-pack.command.json", dict(data)),
        ]
        result = MODULE.slowest_commands(entries)
        self.assertEqual(1, len(result))
        self.assertEqual("02-neutral-pack @ browser-process-nunit", result[0]["label"])
        self.assertEqual("artifacts/browser-process-nunit-0123456789abcdef0123456789abcdef/logs/02-neutral-pack.command.json", result[0]["evidence"])
        self.assertEqual(["pack"], result[0]["key_arguments"])
        self.assertEqual(0, result[0]["exit_code"])

    def test_slowest_commands_labels_tests_and_filters_path_arguments(self):
        entries = [
            ("artifacts/browser-process-nunit-0123456789abcdef0123456789abcdef/ConsumerBuildCreatesMatchingHelperAndCompleteNativeAssets-0123456789abcdef0123456789abcdef/logs/01-default-build.command.json",
             dict(Name="default-build", PID=1, ExitCode=0, DurationSeconds=9.5, Arguments=["build", "F:\\work\\Consumer.csproj", "-m:1", "-p:ArtifactsPath=F:\\work"])),
            ("artifacts/runtime-assets-0123456789abcdef0123456789abcdef/01-select-win-arm64.command.json",
             dict(Name="select-win-arm64", PID=2, ExitCode=0, DurationSeconds=1.0, Arguments=["msbuild", "Probe.proj", "-t:Generate"])),
        ]
        result = MODULE.slowest_commands(entries)
        self.assertEqual("01-default-build @ ConsumerBuildCreatesMatchingHelperAndCompleteNativeAssets", result[0]["label"])
        self.assertEqual(["build", "-m:1"], result[0]["key_arguments"])
        self.assertEqual("01-select-win-arm64 @ runtime-assets", result[1]["label"])

    def test_slowest_commands_keeps_the_twenty_slowest(self):
        entries = [(f"ws/logs/{index:02d}-cmd.command.json", dict(Name=f"cmd{index}", PID=index, ExitCode=1, DurationSeconds=float(index), Arguments=[])) for index in range(25)]
        result = MODULE.slowest_commands(entries)
        self.assertEqual(20, len(result))
        self.assertEqual("cmd24", result[0]["command"])
        self.assertEqual(1, result[-1]["exit_code"])

    def test_workflow_summarizes_with_and_without_raw_command_logs(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        step = workflow.split("      - name: Summarize warnings and test phase timings\n", 1)[1].split("\n      - name:", 1)[0]
        code = "\n".join(line[10:] for line in step.split("        run: |\n", 1)[1].splitlines())
        for has_commands in (True, False):
            with self.subTest(has_commands=has_commands), tempfile.TemporaryDirectory() as directory:
                workspace = Path(directory)
                script = workspace / ".github/scripts/summarize-ci-diagnostics.py"
                script.parent.mkdir(parents=True)
                script.write_bytes((ROOT / ".github/scripts/summarize-ci-diagnostics.py").read_bytes())
                (workspace / "artifacts/test-results").mkdir(parents=True)
                (workspace / "artifacts/build-warnings.log").write_text("", encoding="utf-8")
                if has_commands:
                    log = workspace / "artifacts/browser-process-nunit-0123456789abcdef0123456789abcdef/logs/02-neutral-pack.command.json"
                    log.parent.mkdir(parents=True)
                    log.write_text(json.dumps(dict(Name="neutral-pack", PID=42, ExitCode=0, DurationSeconds=78.9, Arguments=["pack"])), encoding="utf-8")
                result = subprocess.run([sys.executable, "-c", code], cwd=workspace, capture_output=True, text=True, encoding="utf-8")
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                report = json.loads((workspace / "artifacts/ci-diagnostics.json").read_text(encoding="utf-8"))
                commands = report["slowest_commands"]
                if has_commands:
                    self.assertEqual(1, len(commands))
                    self.assertEqual("neutral-pack", commands[0]["command"])
                    self.assertEqual(78.9, commands[0]["seconds"])
                    self.assertEqual(0, commands[0]["exit_code"])
                    self.assertEqual(log.relative_to(workspace).as_posix(), commands[0]["evidence"])
                else:
                    self.assertEqual([], commands)


if __name__ == "__main__":
    unittest.main()
