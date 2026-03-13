from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from azure.ai.evaluation import BleuScoreEvaluator, F1ScoreEvaluator, evaluate

from evaluators import (
    LatencyEvaluator,
    OutputFormatValidityEvaluator,
    SubtitleTimingAlignmentEvaluator,
    TokenF1Evaluator,
)


def build_evaluators() -> dict[str, Any]:
    return {
        "transcript_f1_builtin": F1ScoreEvaluator(),
        "translation_bleu_builtin": BleuScoreEvaluator(),
        "transcript_token_f1": TokenF1Evaluator(metric_name="transcript_token_f1_score"),
        "translation_token_f1": TokenF1Evaluator(metric_name="translation_token_f1_score"),
        "timing_alignment": SubtitleTimingAlignmentEvaluator(),
        "latency": LatencyEvaluator(),
        "format_validity": OutputFormatValidityEvaluator(),
    }


def build_evaluator_config() -> dict[str, dict[str, dict[str, str]]]:
    return {
        "transcript_f1_builtin": {
            "column_mapping": {
                "response": "${data.generated_transcript}",
                "ground_truth": "${data.reference_transcript}",
            }
        },
        "translation_bleu_builtin": {
            "column_mapping": {
                "response": "${data.generated_translation}",
                "ground_truth": "${data.reference_translation}",
            }
        },
        "transcript_token_f1": {
            "column_mapping": {
                "response": "${data.generated_transcript}",
                "ground_truth": "${data.reference_transcript}",
            }
        },
        "translation_token_f1": {
            "column_mapping": {
                "response": "${data.generated_translation}",
                "ground_truth": "${data.reference_translation}",
            }
        },
        "timing_alignment": {
            "column_mapping": {
                "generated_cues": "${data.generated_cues}",
                "reference_cues": "${data.reference_cues}",
            }
        },
        "latency": {
            "column_mapping": {
                "latency_ms": "${data.latency_ms}",
            }
        },
        "format_validity": {
            "column_mapping": {
                "output_format_valid": "${data.output_format_valid}",
                "generated_cues": "${data.generated_cues}",
                "generated_transcript": "${data.generated_transcript}",
                "generated_translation": "${data.generated_translation}",
            }
        },
    }


def write_summary(result: dict[str, Any], summary_path: Path) -> None:
    metrics = result.get("metrics", {})
    lines = ["# Evaluation Summary", ""]
    if not metrics:
        lines.append("No aggregate metrics were produced.")
    else:
        for key in sorted(metrics.keys()):
            lines.append(f"- {key}: {metrics[key]}")

    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def run(data_path: Path, output_json: Path, summary_md: Path, strict: bool) -> None:
    evaluators = build_evaluators()
    evaluator_config = build_evaluator_config()

    result = evaluate(
        data=str(data_path),
        evaluators=evaluators,
        evaluator_config=evaluator_config,
        output_path=str(output_json),
        fail_on_evaluator_errors=strict,
        tags={
            "workspace": "Babel-Player",
            "evaluation_profile": "subtitle_quality_local",
        },
    )

    if hasattr(result, "__dict__"):
        result_data = result.__dict__
    elif isinstance(result, dict):
        result_data = result
    else:
        result_data = json.loads(json.dumps(result, default=str))

    write_summary(result_data, summary_md)


def main() -> None:
    parser = argparse.ArgumentParser(description="Run local evaluation for Babel-Player subtitle workflows.")
    parser.add_argument("--data", default="eval/outputs/responses.jsonl", help="Input JSONL with collected responses")
    parser.add_argument("--output-json", default="eval/outputs/evaluation_result.json", help="Evaluation JSON output")
    parser.add_argument("--summary", default="eval/outputs/evaluation_summary.md", help="Markdown summary output")
    parser.add_argument("--strict", action="store_true", help="Fail the run if any evaluator errors occur")
    args = parser.parse_args()

    run(Path(args.data), Path(args.output_json), Path(args.summary), strict=args.strict)


if __name__ == "__main__":
    main()
