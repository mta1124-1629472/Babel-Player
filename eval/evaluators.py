from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Iterable


def _safe_text(value: Any) -> str:
    return "" if value is None else str(value).strip()


def _tokenize(value: str) -> list[str]:
    return [token for token in value.lower().split() if token]


@dataclass
class LatencyEvaluator:
    good_threshold_ms: float = 2500.0
    hard_limit_ms: float = 12000.0

    def __call__(self, *, latency_ms: Any = None) -> dict[str, float | bool]:
        try:
            latency = float(latency_ms)
        except (TypeError, ValueError):
            return {
                "latency_score": 0.0,
                "latency_ms_value": -1.0,
                "latency_pass": False,
            }

        if latency <= self.good_threshold_ms:
            score = 1.0
        elif latency >= self.hard_limit_ms:
            score = 0.0
        else:
            span = self.hard_limit_ms - self.good_threshold_ms
            score = max(0.0, 1.0 - ((latency - self.good_threshold_ms) / span))

        return {
            "latency_score": round(score, 6),
            "latency_ms_value": latency,
            "latency_pass": latency <= self.hard_limit_ms,
        }


@dataclass
class OutputFormatValidityEvaluator:
    def __call__(
        self,
        *,
        output_format_valid: Any = None,
        generated_cues: Any = None,
        generated_transcript: Any = None,
        generated_translation: Any = None,
    ) -> dict[str, float | bool]:
        declared_valid = bool(output_format_valid)
        cues_valid = self._validate_cues(generated_cues)
        transcript_ok = bool(_safe_text(generated_transcript))
        translation_ok = bool(_safe_text(generated_translation))
        all_valid = declared_valid and cues_valid and transcript_ok and translation_ok

        return {
            "format_validity_score": 1.0 if all_valid else 0.0,
            "format_validity_pass": all_valid,
            "format_declared_valid": declared_valid,
            "format_cues_valid": cues_valid,
        }

    @staticmethod
    def _validate_cues(cues: Any) -> bool:
        if not isinstance(cues, list) or len(cues) == 0:
            return False

        previous_end = -1
        for cue in cues:
            if not isinstance(cue, dict):
                return False
            if "start_ms" not in cue or "end_ms" not in cue or "text" not in cue:
                return False
            try:
                start_ms = int(cue["start_ms"])
                end_ms = int(cue["end_ms"])
            except (TypeError, ValueError):
                return False
            text = _safe_text(cue["text"])
            if start_ms < 0 or end_ms <= start_ms or not text:
                return False
            if previous_end > start_ms:
                return False
            previous_end = end_ms
        return True


@dataclass
class SubtitleTimingAlignmentEvaluator:
    penalty_cap_ms: float = 4000.0

    def __call__(self, *, generated_cues: Any = None, reference_cues: Any = None) -> dict[str, float | bool]:
        if not isinstance(generated_cues, list) or not isinstance(reference_cues, list):
            return {
                "timing_alignment_score": 0.0,
                "timing_alignment_mae_ms": self.penalty_cap_ms,
                "timing_alignment_pass": False,
            }

        if len(generated_cues) == 0 or len(reference_cues) == 0:
            return {
                "timing_alignment_score": 0.0,
                "timing_alignment_mae_ms": self.penalty_cap_ms,
                "timing_alignment_pass": False,
            }

        paired = zip(generated_cues, reference_cues)
        errors: list[float] = []
        pair_count = 0
        for generated, reference in paired:
            pair_count += 1
            gen_start, gen_end = self._extract_bounds(generated)
            ref_start, ref_end = self._extract_bounds(reference)
            if gen_start is None or gen_end is None or ref_start is None or ref_end is None:
                errors.append(self.penalty_cap_ms)
                continue
            errors.append(abs(gen_start - ref_start))
            errors.append(abs(gen_end - ref_end))

        cue_count_delta = abs(len(generated_cues) - len(reference_cues))
        if cue_count_delta > 0:
            # Penalize missing/extra cues by adding synthetic boundary errors.
            errors.extend([self.penalty_cap_ms] * (2 * cue_count_delta))

        if pair_count == 0:
            return {
                "timing_alignment_score": 0.0,
                "timing_alignment_mae_ms": self.penalty_cap_ms,
                "timing_alignment_pass": False,
            }

        mae = sum(errors) / len(errors)
        normalized = min(mae, self.penalty_cap_ms) / self.penalty_cap_ms
        score = 1.0 - normalized
        return {
            "timing_alignment_score": round(max(score, 0.0), 6),
            "timing_alignment_mae_ms": round(mae, 3),
            "timing_alignment_pass": mae <= 750.0 and cue_count_delta == 0,
            "timing_alignment_cue_count_delta": cue_count_delta,
        }

    @staticmethod
    def _extract_bounds(cue: Any) -> tuple[int | None, int | None]:
        if not isinstance(cue, dict):
            return None, None
        try:
            return int(cue["start_ms"]), int(cue["end_ms"])
        except (KeyError, TypeError, ValueError):
            return None, None


@dataclass
class TokenF1Evaluator:
    metric_name: str

    def __call__(self, *, response: Any = None, ground_truth: Any = None) -> dict[str, float]:
        response_tokens = _tokenize(_safe_text(response))
        truth_tokens = _tokenize(_safe_text(ground_truth))
        score = self._token_f1(response_tokens, truth_tokens)
        return {self.metric_name: round(score, 6)}

    @staticmethod
    def _token_f1(response_tokens: Iterable[str], truth_tokens: Iterable[str]) -> float:
        response_list = list(response_tokens)
        truth_list = list(truth_tokens)
        if len(response_list) == 0 and len(truth_list) == 0:
            return 1.0
        if len(response_list) == 0 or len(truth_list) == 0:
            return 0.0

        response_counts: dict[str, int] = {}
        for token in response_list:
            response_counts[token] = response_counts.get(token, 0) + 1

        truth_counts: dict[str, int] = {}
        for token in truth_list:
            truth_counts[token] = truth_counts.get(token, 0) + 1

        overlap = 0
        for token, count in response_counts.items():
            if token in truth_counts:
                overlap += min(count, truth_counts[token])

        if overlap == 0:
            return 0.0

        precision = overlap / len(response_list)
        recall = overlap / len(truth_list)
        return (2 * precision * recall) / (precision + recall)
