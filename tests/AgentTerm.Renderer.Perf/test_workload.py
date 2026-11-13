import contextlib
import io
import json
import pathlib
import runpy
import sys
import tempfile
import time
import unittest
from unittest import mock


WORKLOAD = runpy.run_path(str(pathlib.Path(__file__).resolve().parents[2] / "scripts/terminal-perf-workload.py"))


class WorkloadProtocolTests(unittest.TestCase):
    def run_workload(self, directory):
        args = ["terminal-perf-workload.py", str(directory), "--hz", "1", "--seconds", "1", "--trials", "2"]
        with mock.patch.object(sys, "argv", args), contextlib.redirect_stdout(io.StringIO()):
            WORKLOAD["main"]()

    def test_start_signals_may_arrive_before_the_trial_wait(self):
        write_text = pathlib.Path.write_text
        replace = pathlib.Path.replace
        for early_trial in (1, 2):
            with self.subTest(early_trial=early_trial), tempfile.TemporaryDirectory() as temporary:
                directory = pathlib.Path(temporary)

                def publish_ready(path, text, *args, **kwargs):
                    result = write_text(path, text, *args, **kwargs)
                    if path.name == "ready.json" and early_trial == 1:
                        write_text(directory / "run-1.go", "go")
                    return result

                def publish_done(path, target):
                    result = replace(path, target)
                    if target.name == "run-1.done.json":
                        write_text(directory / "run-2.go", "go")
                    return result

                def release_first_gate(_):
                    if not (directory / "run-1.go").exists():
                        write_text(directory / "run-1.go", "go")

                with (
                    mock.patch.object(pathlib.Path, "write_text", publish_ready),
                    mock.patch.object(pathlib.Path, "replace", publish_done),
                    mock.patch.object(time, "sleep", release_first_gate),
                    mock.patch.object(time, "monotonic", return_value=0),
                ):
                    self.run_workload(directory)

                for trial in (1, 2):
                    done = json.loads((directory / f"run-{trial}.done.json").read_text())
                    self.assertEqual(done["ticks"], 1)
                    self.assertEqual(done["expectedTicks"], 1)

    def test_previous_run_signals_still_reject_the_directory(self):
        for name in ("run-1.go", "run-1.done.json"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                directory = pathlib.Path(temporary)
                (directory / name).write_text("stale")
                with self.assertRaises(FileExistsError):
                    self.run_workload(directory)


if __name__ == "__main__":
    unittest.main()
