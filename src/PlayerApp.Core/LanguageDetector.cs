// PSEUDOCODE (detailed plan):
// 1. Provide a small, self-contained static class `LanguageDetector` in the `PlayerApp.Core` namespace
// 2. Implement a public static method `Detect(string text)` that returns a short language code (e.g. "en", "zh", "ja", "ru", "ar", "und").
// 3. Detection strategy (simple, deterministic heuristics):
//    a. If `text` is null or whitespace -> return "en" (safe default).
//    b. Take a short sample of the text (first 200 chars) to limit work.
//    c. Inspect Unicode codepoints in the sample:
//       - If any CJK Unified Ideographs -> return "zh"
//       - If any Hiragana/Katakana -> return "ja"
//       - If any Cyrillic -> return "ru"
//       - If any Arabic -> return "ar"
//    d. If no script indication found, lower-case the sample and look for common English stopwords (" the ", " and ", " is ", " of ", " to ").
//       - If found -> return "en"
//    e. If still ambiguous, check if all characters are ASCII -> return "en", else return "und" (undetermined).
// 4. Keep implementation minimal and safe so it compiles and fixes the CS0103 error when `LanguageDetector.Detect(...)` is referenced.
//
// This file implements that plan.

using System;

namespace PlayerApp.Core
{
	public static class LanguageDetector
	{
		/// <summary>
		/// Naive language detection using Unicode script ranges and simple keyword heuristics.
		/// Returns short language codes such as "en", "zh", "ja", "ru", "ar", or "und" (undetermined).
		/// This is intentionally lightweight to serve as a fallback for the missing symbol.
		/// </summary>
		/// <param name="text">Input text to detect language for.</param>
		/// <returns>Language code string.</returns>
		public static string Detect(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "en"; // safe default for empty input
			}

			// Use a short sample to reduce work
			var sample = text.Length > 200 ? text.Substring(0, 200) : text;

			foreach (var ch in sample)
			{
				int code = ch;
				// CJK Unified Ideographs (common Chinese characters)
				if (code >= 0x4E00 && code <= 0x9FFF) return "zh";
				// Hiragana and Katakana (Japanese)
				if ((code >= 0x3040 && code <= 0x309F) || (code >= 0x30A0 && code <= 0x30FF)) return "ja";
				// Cyrillic (Russian and others)
				if (code >= 0x0400 && code <= 0x04FF) return "ru";
				// Arabic
				if (code >= 0x0600 && code <= 0x06FF) return "ar";
			}

			// Simple English keyword heuristics
			var lower = sample.ToLowerInvariant();
			string[] enHints = new[] { " the ", " and ", " is ", " of ", " to ", " in " };
			foreach (var hint in enHints)
			{
				if (lower.Contains(hint)) return "en";
			}

			// If all characters are ASCII, assume English as a pragmatic default
			bool allAscii = true;
			foreach (var ch in sample)
			{
				if (ch > 127)
				{
					allAscii = false;
					break;
				}
			}

			return allAscii ? "en" : "und";
		}
	}
}