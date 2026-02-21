namespace PlayerApp.Core
{
	public class MtService
	{
		public void LoadModel(string modelPath)
		{
			// Initialize ONNX Runtime session with DirectML/CUDA/CPU provider as available.
		}

		public string Translate(string text)
		{
			// Tokenize -> run ONNX session -> detokenize -> return English text.
			return "[translated] " + text;
		}
	}
}
