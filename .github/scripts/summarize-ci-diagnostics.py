"""Summarize compiler warnings, measured test phases, and build command timings from CI logs or ZIPs."""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
from zipfile import ZipFile


WARNING = re.compile(r"((?:src|tests)[/\\].+?)\((\d+),(\d+)\): warning (CS\d+): (.*?)(?: \[.*\])?$")
PHASE = re.compile(r"ticks=(\d+) frequency=(\d+) pid=(\d+) thread=\d+ test=(.*?) stage=(\S+)")
GUID_SUFFIX = re.compile(r"-[0-9a-f]{32}$")
PERFORMANCE_SECTIONS = {
    "Project Evaluation Performance Summary:": "project_evaluation",
    "Project Performance Summary:": "projects",
    "Target Performance Summary:": "targets",
    "Task Performance Summary:": "tasks",
}
# Project summaries contain more deeply indented per-target subtotals; those are not projects.
PERFORMANCE_ROW = re.compile(r"^ {0,8}(\d+) ms\s+(.+?)\s+(\d+) calls\s*$")
SIGNALS = {
    "objc_duplicate_class": "Class ExtensionDropdownHandler is implemented in both",
    "shader_compile_timeout": "Compilation took longer than",
    "gpu_initialization_exit": "Exiting GPU process due to errors during initialization",
    "mach_rendezvous_failure": "Mach rendezvous failed",
}


def logs(path):
    if path.suffix == ".zip":
        with ZipFile(path) as archive:
            for name in archive.namelist():
                if name.endswith(".log"):
                    yield f"{path.name}/{name}", archive.read(name).decode("utf-8-sig")
    elif path.is_dir():
        for child in sorted(path.rglob("*.log")):
            yield str(child), child.read_text(encoding="utf-8-sig")
    else:
        yield str(path), path.read_text(encoding="utf-8-sig")


def command_logs(path):
    if path.suffix == ".zip":
        with ZipFile(path) as archive:
            for name in archive.namelist():
                if name.endswith(".command.json"):
                    yield f"{path.name}/{name}", json.loads(archive.read(name))
    elif path.is_dir():
        for child in sorted(path.rglob("*.command.json")):
            yield str(child).replace("\\", "/"), json.loads(child.read_text(encoding="utf-8-sig"))
    elif path.name.endswith(".command.json"):
        yield str(path).replace("\\", "/"), json.loads(path.read_text(encoding="utf-8-sig"))


def command_label(location):
    # Attachment copies live under In/<guid>/ZM/ without a test identity; only logs/ owners
    # and guid-suffixed workspace roots contribute a context.
    parts = location.replace("\\", "/").split("/")
    stem = parts[-1][: -len(".command.json")]
    if "logs" in parts:
        index = parts.index("logs")
        if index > 0:
            return f"{stem} @ {GUID_SUFFIX.sub('', parts[index - 1])}"
    elif len(parts) > 1 and GUID_SUFFIX.search(parts[-2]):
        return f"{stem} @ {GUID_SUFFIX.sub('', parts[-2])}"
    return stem


def slowest_commands(entries, limit=20):
    commands = []
    seen = set()
    for location, data in entries:
        key = json.dumps(data, sort_keys=True)
        if key in seen:  # Attachment copies repeat raw workspace records verbatim.
            continue
        seen.add(key)
        commands.append(dict(
            command=data.get("Name"),
            label=command_label(location),
            exit_code=data.get("ExitCode"),
            seconds=data.get("DurationSeconds") or 0.0,
            key_arguments=[argument for argument in data.get("Arguments") or [] if "/" not in argument and "\\" not in argument],
            evidence=location,
        ))
    return sorted(commands, key=lambda command: command["seconds"], reverse=True)[:limit]


def msbuild_performance(contents):
    sections = {}
    section = None
    for line in contents.splitlines():
        if line in PERFORMANCE_SECTIONS:
            section = sections.setdefault(PERFORMANCE_SECTIONS[line], {})
        elif line and not line[0].isspace():
            section = None
        elif section is not None:
            match = PERFORMANCE_ROW.match(line)
            if match:
                milliseconds, name, calls = match.groups()
                row = section.setdefault(name, dict(name=name, milliseconds=0, calls=0))
                row["milliseconds"] += int(milliseconds)
                row["calls"] += int(calls)
    result = {}
    for name, rows in sections.items():
        ordered = sorted(rows.values(), key=lambda row: row["milliseconds"], reverse=True)
        # Keep every task so even a short Csc invocation remains distinguishable from no invocation.
        if name == "targets":
            ordered = ordered[:20]
        result[name] = [dict(name=row["name"], seconds=row["milliseconds"] / 1000, calls=row["calls"]) for row in ordered]
    return result


def add_command_performance(commands, entries):
    by_log = {command["evidence"].removesuffix(".command.json") + ".stdout.log": command for command in commands}
    for location, contents in entries:
        location = location.replace("\\", "/")
        command = by_log.get(location)
        if command is not None:
            performance = msbuild_performance(contents)
            if performance:
                # Project and target times include nested work; tasks aggregate across all projects.
                command["msbuild_performance"] = dict(evidence=location, project_times_include_dependencies=True, task_times_are_command_totals=True, **performance)


def summarize(entries, root):
    warnings = {}
    intervals = []
    signals = []
    for name, contents in entries:
        phases = {}
        counts = Counter()
        for line in contents.splitlines():
            match = WARNING.search(line)
            if match:
                file, row, column, code, message = match.groups()
                file = file.replace("\\", "/")
                key = (file, int(row), int(column), code)
                if key not in warnings:
                    source = root / file
                    generated = None
                    if source.is_file():
                        with source.open(encoding="utf-8-sig") as stream:
                            generated = "<auto-generated" in stream.read(2048).lower()
                    warnings[key] = dict(file=file, line=int(row), column=int(column), code=code, message=message, generated_marker=generated, occurrences=0)
                warnings[key]["occurrences"] += 1
            # stdout repeats these records; only the dedicated phase file is authoritative.
            if Path(name).name.startswith("test-phases-"):
                match = PHASE.search(line)
                if match:
                    ticks, frequency, pid, test, stage = match.groups()
                    key = (pid, test)
                    phases.setdefault(key, []).append((int(ticks), int(frequency), stage))
            if name.endswith((".stderr.log", "cef-tests.log")):
                for signal, text in SIGNALS.items():
                    if text in line:
                        counts[signal] += 1
        for (_, test), records in phases.items():
            # Threads capture timestamps before acquiring the synchronized writer.
            records.sort(key=lambda record: record[0])
            for previous, current in zip(records, records[1:]):
                old_ticks, old_frequency, old_stage = previous
                ticks, frequency, stage = current
                if frequency != old_frequency:
                    raise ValueError(f"Invalid monotonic phase timestamps in {name}")
                intervals.append(dict(log=name, test=test, start=old_stage, end=stage, seconds=(ticks - old_ticks) / frequency))
        if counts:
            signals.append(dict(log=name, matching_lines=dict(counts)))
    ordered = [warnings[key] for key in sorted(warnings)]
    return dict(
        unique_warning_count=len(ordered),
        warnings_by_code=dict(sorted(Counter(item["code"] for item in ordered).items())),
        warnings=ordered,
        slowest_phase_intervals=sorted(intervals, key=lambda item: item["seconds"], reverse=True)[:50],
        native_log_signals=signals,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--commands", action="append", type=Path, default=[], help="paths with raw *.command.json records; they take precedence over attachment copies found in inputs")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if any(not path.exists() for path in [*args.inputs, *args.commands]):
        parser.error("Every input path must exist.")
    if args.output.exists() or not args.output.parent.is_dir():
        parser.error("The output must be new and its parent must exist.")
    root = Path(__file__).resolve().parents[2]
    report = summarize((entry for path in args.inputs for entry in logs(path)), root)
    report["slowest_commands"] = slowest_commands((entry for path in [*args.commands, *args.inputs] for entry in command_logs(path)))
    add_command_performance(report["slowest_commands"], (entry for path in [*args.commands, *args.inputs] for entry in logs(path)))
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(report, output, ensure_ascii=False, indent=2)
    if args.output.stat().st_size == 0:
        raise RuntimeError("Missing diagnostics summary")
    print(f"Unique warnings: {report['unique_warning_count']}; by code: {report['warnings_by_code']}")
    for phase in report["slowest_phase_intervals"][:10]:
        print(f"{phase['seconds']:.3f}s {phase['test']}: {phase['start']} -> {phase['end']}")
    for command in report["slowest_commands"][:10]:
        print(f"{command['seconds']:.1f}s {command['label']} (exit {command['exit_code']})")
    print(f"Diagnostics: {args.output.resolve()}")


if __name__ == "__main__":
    main()
