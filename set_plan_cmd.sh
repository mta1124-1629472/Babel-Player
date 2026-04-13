set_plan "1. **Implement Parakeet ASR endpoint in Python.**
   - Use \`run_in_bash_session\` to insert the \`POST /transcribe/parakeet\` endpoint using the NeMo Parakeet-TDT-0.6B-v3 model into \`inference/main.py\`.
   - Run \`grep '/transcribe/parakeet' inference/main.py\` to verify the code is correctly added.
2. **Implement Parakeet C# Provider.**
   - Use \`run_in_bash_session\` to create \`Services/ParakeetTranscriptionProvider.cs\` which implements \`ITranscriptionProvider\` and calls the new python endpoint. It should be similar to \`ContainerizedTranscriptionProvider\`.
   - Run \`cat Services/ParakeetTranscriptionProvider.cs\` to verify the code is correctly added.
3. **Register Parakeet in C#.**
   - Use \`run_in_bash_session\` to update \`Models/ProviderNames.cs\` with the \`Parakeet\` constant.
   - Use \`run_in_bash_session\` to update \`Services/Registries/TranscriptionRegistry.cs\` to register \`ParakeetTranscriptionProvider\`.
   - Run \`dotnet build Babel-Player.sln\` to confirm the changes compile.
4. **Run tests.**
   - Use \`run_in_bash_session\` to run \`dotnet test Babel-Player.sln\` to verify all tests pass.
5. **Pre-commit step.**
   - Complete pre-commit steps to ensure proper testing, verification, review, and reflection are done."
