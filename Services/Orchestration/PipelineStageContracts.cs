using System;
using Babel.Player.Models;

namespace Babel.Player.Services.Orchestration;

public sealed record PipelineStageUpdate(
    int StageIndex,
    int StageCount,
    SessionWorkflowStage TargetStage,
    string Title,
    string Detail,
    double Progress01,
    bool IsIndeterminate,
    string? StreamingStatus = null);

public readonly record struct PipelineStageContext(
    int StageIndex,
    int StageCount,
    SessionWorkflowStage TargetStage,
    string Title,
    IProgress<PipelineStageUpdate>? Reporter);
