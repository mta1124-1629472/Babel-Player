using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Planning;

public enum InferenceStage
{
    Transcription,
    Translation,
    Tts,
    Diarization,
}

public enum RuntimeRole
{
    CpuNlp,
    CpuVoice,
    CpuDiar,
    Containerized,
    Cloud,
}

public sealed record ExecutionPlanRequest(
    InferenceStage Stage,
    AppSettings Settings,
    ApiKeyStore? KeyStore,
    HardwareSnapshot HardwareSnapshot);

public sealed record StageExecutionPlan(
    InferenceStage Stage,
    string ProviderId,
    InferenceRuntime Runtime,
    ComputeProfile Profile,
    RuntimeRole Role,
    bool IsFallback,
    string Reason);

public interface IExecutionPlanner
{
    StageExecutionPlan CreatePlan(ExecutionPlanRequest request);
}
