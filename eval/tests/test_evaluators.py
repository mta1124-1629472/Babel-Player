from __future__ import annotations

import sys
from pathlib import Path
import unittest


EVAL_DIR = Path(__file__).resolve().parents[1]
if str(EVAL_DIR) not in sys.path:
    sys.path.insert(0, str(EVAL_DIR))

from evaluators import (  # noqa: E402
    LatencyEvaluator,
    OutputFormatValidityEvaluator,
    SubtitleTimingAlignmentEvaluator,
    TokenF1Evaluator,
)


class EvaluatorTests(unittest.TestCase):
    def test_latency_evaluator_handles_invalid_value(self) -> None:
        evaluator = LatencyEvaluator()
        result = evaluator(latency_ms="bad")
        self.assertEqual(result["latency_score"], 0.0)
        self.assertFalse(result["latency_pass"])

    def test_latency_evaluator_interpolates_mid_range(self) -> None:
        evaluator = LatencyEvaluator(good_threshold_ms=2500, hard_limit_ms=12000)
        result = evaluator(latency_ms=7250)
        self.assertAlmostEqual(result["latency_score"], 0.5, places=6)
        self.assertTrue(result["latency_pass"])

    def test_format_validity_rejects_overlapping_cues(self) -> None:
        evaluator = OutputFormatValidityEvaluator()
        result = evaluator(
            output_format_valid=True,
            generated_transcript="hello",
            generated_translation="hola",
            generated_cues=[
                {"start_ms": 0, "end_ms": 1000, "text": "a"},
                {"start_ms": 900, "end_ms": 1200, "text": "b"},
            ],
        )
        self.assertEqual(result["format_validity_score"], 0.0)
        self.assertFalse(result["format_validity_pass"])
        self.assertFalse(result["format_cues_valid"])

    def test_timing_alignment_penalizes_count_mismatch(self) -> None:
        evaluator = SubtitleTimingAlignmentEvaluator(penalty_cap_ms=4000)
        result = evaluator(
            generated_cues=[{"start_ms": 0, "end_ms": 100, "text": "a"}],
            reference_cues=[
                {"start_ms": 0, "end_ms": 100, "text": "a"},
                {"start_ms": 150, "end_ms": 300, "text": "b"},
            ],
        )
        self.assertEqual(result["timing_alignment_cue_count_delta"], 1)
        self.assertFalse(result["timing_alignment_pass"])
        self.assertLess(result["timing_alignment_score"], 1.0)

    def test_token_f1_exact_match(self) -> None:
        evaluator = TokenF1Evaluator(metric_name="score")
        result = evaluator(response="good morning", ground_truth="good morning")
        self.assertEqual(result["score"], 1.0)


if __name__ == "__main__":
    unittest.main()
