from __future__ import annotations

import sys
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


EVAL_DIR = Path(__file__).resolve().parents[1]
if str(EVAL_DIR) not in sys.path:
    sys.path.insert(0, str(EVAL_DIR))

import run_evaluation  # noqa: E402


class RunEvaluationTests(unittest.TestCase):
    @patch("run_evaluation.evaluate")
    def test_run_passes_strict_flag(self, mock_evaluate) -> None:
        mock_evaluate.return_value = {
            "metrics": {"x": 1.0},
            "rows": [],
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            data = temp_path / "data.jsonl"
            output_json = temp_path / "result.json"
            summary_md = temp_path / "summary.md"
            data.write_text("{}\n", encoding="utf-8")

            run_evaluation.run(data, output_json, summary_md, strict=True)

            self.assertTrue(mock_evaluate.called)
            kwargs = mock_evaluate.call_args.kwargs
            self.assertTrue(kwargs["fail_on_evaluator_errors"])
            self.assertTrue(summary_md.exists())


if __name__ == "__main__":
    unittest.main()
