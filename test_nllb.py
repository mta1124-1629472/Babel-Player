import torch
from transformers import AutoModelForSeq2SeqLM, AutoTokenizer
import sys

print("Starting model load...")
model_id = "facebook/nllb-200-distilled-600M"
print(f"Loading tokenizer for {model_id}...", flush=True)
tokenizer = AutoTokenizer.from_pretrained(model_id)
print("Tokenizer loaded OK", flush=True)

print("Loading model...", flush=True)
sys.stdout.flush()
model = AutoModelForSeq2SeqLM.from_pretrained(model_id, dtype=torch.float16, device_map="cuda")
print("Model loaded OK", flush=True)
print("Test complete!", flush=True)
