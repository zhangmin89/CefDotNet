import importlib.util
from pathlib import Path
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


if __name__ == "__main__":
    unittest.main()
