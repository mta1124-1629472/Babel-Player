from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path
import unittest


EVAL_DIR = Path(__file__).resolve().parents[1]
if str(EVAL_DIR) not in sys.path:
    sys.path.insert(0, str(EVAL_DIR))

import collect_responses  # noqa: E402


class CollectResponsesTests(unittest.TestCase):
    def test_parse_adapter_command_rejects_empty(self) -> None:
        with self.assertRaises(ValueError):
            collect_responses._parse_adapter_command("")

    def test_run_adapter_raises_on_invalid_json_stdout(self) -> None:
        with self.assertRaises(ValueError):
            collect_responses.run_adapter("cmd /c echo not-json", {"x": 1})

    def test_collect_with_mock_writes_rows(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            temp_path = Path(temp_dir)
            dataset = temp_path / "dataset.jsonl"
            output = temp_path / "out.jsonl"
            rows = [
                {
                    "sample_id": "s1",
                    "reference_transcript": "a",
                    "reference_translation": "b",
                    "reference_cues": [{"start_ms": 0, "end_ms": 100, "text": "a"}],
                    "latency_ms": 50,
                }
            ]
            dataset.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")

            collect_responses.collect(dataset, output, adapter_cmd=None)

            written = collect_responses.load_jsonl(output)
            self.assertEqual(len(written), 1)
            self.assertEqual(written[0]["generated_transcript"], "a")
            self.assertEqual(written[0]["generated_translation"], "b")


if __name__ == "__main__":
    unittest.main()
