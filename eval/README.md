# Babel-Player Evaluation Framework

This directory provides a local evaluation framework for subtitle/transcription workflows.

## Included Metrics

- transcription accuracy (built-in F1 + deterministic token F1)
- translation quality (built-in BLEU + deterministic token F1)
- subtitle timing alignment (custom cue timing evaluator)
- latency/performance (custom latency evaluator)
- output format validity (custom schema and cue validation)

## Files

- `datasets/sample_eval_dataset.jsonl`: starter dataset in JSONL format
- `collect_responses.py`: collects generated outputs by running an adapter command or mock mode
- `evaluators.py`: custom evaluator implementations
- `run_evaluation.py`: executes unified evaluation via `azure.ai.evaluation.evaluate()`
- `requirements.txt`: Python dependencies

## Dataset Contract

Each JSONL row should include at least these fields:

- `sample_id`
- `query`
- `media_path`
- `reference_transcript`
- `generated_transcript`
- `reference_translation`
- `generated_translation`
- `reference_cues` (list of `{start_ms, end_ms, text}`)
- `generated_cues` (list of `{start_ms, end_ms, text}`)
- `latency_ms`
- `output_format_valid`

## Quick Start

1. Install dependencies:

```powershell
pip install -r eval/requirements.txt
```

1. Collect responses using an adapter command that reads one JSON item from stdin and returns one JSON object on stdout:

```powershell
python eval/collect_responses.py --dataset eval/datasets/sample_eval_dataset.jsonl --output eval/outputs/responses.jsonl --adapter-cmd "python your_adapter.py"
```

If you do not pass `--adapter-cmd`, mock mode copies generated fields from references.

1. Run evaluation:

```powershell
python eval/run_evaluation.py --data eval/outputs/responses.jsonl --output-json eval/outputs/evaluation_result.json --summary eval/outputs/evaluation_summary.md
```

## Notes

- Runtime is configured for local evaluation.
- This framework intentionally uses both built-in and deterministic custom metrics so you can run without requiring a judge model configuration.
- You can extend evaluators in `evaluators.py` without changing shell/app architecture.
